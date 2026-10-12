/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Physics;

/// <summary>
/// A mesh asset carries a hull list and a bounding hull. The mesher must hand out the ones that belong to the
/// mesh that was asked for, also when that mesh is served from its cache and no asset is decoded.
/// </summary>
public class MesherHullListTests
{
    private static readonly Vector3 Size = new Vector3(2f, 3f, 4f);
    private const float Lod = 32f;

    // Three assets: 3 hulls, 1 hull, and 3 different hulls.
    private static readonly Vector3[][] ThreeHulls = Boxes((0f, 0f, 0f, 1f, 1f, 1f), (2f, 0f, 0f, 3f, 1f, 1f), (0f, 2f, 0f, 1f, 3f, 2f));
    private static readonly Vector3[][] OneHull = Boxes((-1f, -1f, -1f, 0.5f, 0.25f, 0.75f));
    private static readonly Vector3[][] OtherThreeHulls = Boxes((5f, 5f, 5f, 6f, 6f, 6f), (7f, 5f, 5f, 9f, 6f, 6f), (5f, 7f, 5f, 6f, 8f, 7f));

    // ---------------------------------------------------------------- tests

    [Fact]
    public void Hull_list_asked_after_a_request_is_that_requests_own_also_from_the_cache()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape a = MeshPbs(Asset(ThreeHulls));
        PrimitiveBaseShape b = MeshPbs(Asset(OneHull));
        PrimitiveBaseShape c = MeshPbs(Asset(OtherThreeHulls));

        // First decode of each, then the same requests again in another order (all served from the cache).
        foreach (var (pbs, hulls) in new[] { (a, ThreeHulls), (b, OneHull), (c, OtherThreeHulls) })
        {
            Assert.NotNull(mesher.CreateMesh("first", pbs, Size, Lod));
            AssertHulls(hulls, mesher.GetConvexHulls(Size));
        }

        foreach (var (pbs, hulls) in new[] { (c, OtherThreeHulls), (a, ThreeHulls), (b, OneHull), (a, ThreeHulls) })
        {
            Assert.NotNull(mesher.CreateMesh("again", pbs, Size, Lod));
            AssertHulls(hulls, mesher.GetConvexHulls(Size));
        }
    }

    [Fact]
    public void Hull_list_of_a_given_mesh_is_that_meshs_own_in_any_order_of_asking()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape a = MeshPbs(Asset(ThreeHulls));
        PrimitiveBaseShape b = MeshPbs(Asset(OneHull));
        PrimitiveBaseShape c = MeshPbs(Asset(OtherThreeHulls));

        IMesh meshA = mesher.CreateMesh("a", a, Size, Lod);
        IMesh meshB = mesher.CreateMesh("b", b, Size, Lod);
        IMesh meshC = mesher.CreateMesh("c", c, Size, Lod);
        IMesh meshAFromCache = mesher.CreateMesh("a again", a, Size, Lod);

        AssertHulls(OtherThreeHulls, mesher.GetConvexHulls(meshC, Size));
        AssertHulls(ThreeHulls, mesher.GetConvexHulls(meshA, Size));
        AssertHulls(OneHull, mesher.GetConvexHulls(meshB, Size));
        AssertHulls(ThreeHulls, mesher.GetConvexHulls(meshAFromCache, Size));
        AssertBounding(OneHull, mesher.GetBoundingHull(meshB, Size));
        AssertBounding(OtherThreeHulls, mesher.GetBoundingHull(meshC, Size));
        AssertBounding(ThreeHulls, mesher.GetBoundingHull(meshA, Size));
    }

    [Fact]
    public void A_small_non_physical_mesh_replaced_by_its_bounding_box_keeps_its_hulls()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape a = MeshPbs(Asset(ThreeHulls));
        Vector3 small = new Vector3(0.1f, 0.1f, 0.1f);
        IMesh mesh = mesher.CreateMesh("small", a, small, Lod, false, false);
        Assert.NotNull(mesh);
        List<List<Vector3>>? hulls = mesher.GetConvexHulls(mesh, Vector3.One);
        Assert.NotNull(hulls);
        Assert.Equal(3, hulls!.Count);
    }

    [Fact]
    public void Bounding_hull_asked_after_a_request_is_that_requests_own_also_from_the_cache()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape a = MeshPbs(Asset(ThreeHulls));
        PrimitiveBaseShape b = MeshPbs(Asset(OneHull));

        mesher.CreateMesh("a", a, Size, Lod);
        mesher.CreateMesh("b", b, Size, Lod);
        mesher.CreateMesh("a again", a, Size, Lod);
        AssertBounding(ThreeHulls, mesher.GetBoundingHull(Size));
        mesher.CreateMesh("b again", b, Size, Lod);
        AssertBounding(OneHull, mesher.GetBoundingHull(Size));
    }

    [Fact]
    public void A_request_that_gives_no_mesh_leaves_no_hulls_from_an_earlier_one()
    {
        var mesher = new Meshmerizer();
        mesher.CreateMesh("a", MeshPbs(Asset(ThreeHulls)), Size, Lod);
        Assert.NotNull(mesher.GetConvexHulls(Size));

        PrimitiveBaseShape notLoaded = MeshPbs(Array.Empty<byte>());
        Assert.Null(mesher.CreateMesh("not loaded", notLoaded, Size, Lod));
        Assert.Null(mesher.GetConvexHulls(Size));
        Assert.Null(mesher.GetBoundingHull(Size));
    }

    [Fact]
    public void An_asset_without_a_hull_list_gives_none()
    {
        var mesher = new Meshmerizer();
        mesher.CreateMesh("a", MeshPbs(Asset(ThreeHulls)), Size, Lod);
        mesher.CreateMesh("plain", MeshPbs(Asset(null)), Size, Lod);
        Assert.Null(mesher.GetConvexHulls(Size));
        Assert.Null(mesher.GetBoundingHull(Size));
    }

    [Fact]
    public void Each_thread_gets_the_hulls_of_its_own_last_request()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape a = MeshPbs(Asset(ThreeHulls));
        PrimitiveBaseShape b = MeshPbs(Asset(OneHull));

        mesher.CreateMesh("a", a, Size, Lod);
        var other = Task.Run(() =>
        {
            mesher.CreateMesh("b", b, Size, Lod);
            return mesher.GetConvexHulls(Size);
        });
        AssertHulls(OneHull, other.GetAwaiter().GetResult());
        AssertHulls(ThreeHulls, mesher.GetConvexHulls(Size));
    }

    // ---------------------------------------------------------------- checks

    private static void AssertHulls(Vector3[][] expected, List<List<Vector3>>? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Length, actual[i].Count);
            for (int j = 0; j < expected[i].Length; j++)
                AssertNear(expected[i][j] * Size, actual[i][j]);
        }
    }

    private static void AssertBounding(Vector3[][] hullsOfAsset, List<Vector3>? actual)
    {
        Assert.NotNull(actual);
        (Vector3 mn, Vector3 mx) = Bounds(hullsOfAsset.SelectMany(h => h));
        Assert.Equal(8, actual!.Count);
        (Vector3 amn, Vector3 amx) = Bounds(actual);
        AssertNear(mn * Size, amn);
        AssertNear(mx * Size, amx);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        // 16-bit positions over the asset's own bounds: the error is far below a millimetre.
        Assert.True(Vector3.Distance(expected, actual) < 0.001f, $"expected {expected}, got {actual}");
    }

    // ---------------------------------------------------------------- the asset encoder
    // Writes the viewer's mesh asset format: an LLSD header of block offsets and sizes, then zlib-compressed blocks.

    internal static Vector3[][] Boxes(params (float x0, float y0, float z0, float x1, float y1, float z1)[] boxes)
    {
        return boxes.Select(b =>
        {
            var h = new Vector3[8];
            for (int i = 0; i < 8; i++)
                h[i] = new Vector3((i & 1) == 0 ? b.x0 : b.x1, (i & 2) == 0 ? b.y0 : b.y1, (i & 4) == 0 ? b.z0 : b.z1);
            return h;
        }).ToArray();
    }

    private static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> points)
    {
        Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
        foreach (Vector3 p in points)
        {
            mn = Vector3.Min(mn, p);
            mx = Vector3.Max(mx, p);
        }
        return (mn, mx);
    }

    private static ushort Quantize(float v, float min, float max) =>
        max <= min ? (ushort)0 : (ushort)Math.Clamp(MathF.Round((v - min) / (max - min) * 65535f), 0f, 65535f);

    private static byte[] U16(IEnumerable<ushort> values)
    {
        var bytes = new List<byte>();
        foreach (ushort u in values)
        {
            bytes.Add((byte)(u & 0xFF));
            bytes.Add((byte)(u >> 8));
        }
        return bytes.ToArray();
    }

    private static OSDArray Vec(Vector3 v) => new OSDArray { OSD.FromReal(v.X), OSD.FromReal(v.Y), OSD.FromReal(v.Z) };

    private static byte[] Compress(OSD osd)
    {
        byte[] raw = OSDParser.SerializeLLSDBinary(osd, false);
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); // zlib header, which the reader skips
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
            deflate.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    // A one-triangle display mesh, enough for the mesher to return a mesh.
    private static byte[] MeshBlock()
    {
        Vector3[] verts = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        (Vector3 mn, Vector3 mx) = Bounds(verts);
        var pos = new List<ushort>();
        foreach (Vector3 p in verts)
        {
            pos.Add(Quantize(p.X, mn.X, mx.X));
            pos.Add(Quantize(p.Y, mn.Y, mx.Y));
            pos.Add(Quantize(p.Z, mn.Z, mx.Z));
        }
        var map = new OSDMap
        {
            ["PositionDomain"] = new OSDMap { ["Min"] = Vec(mn), ["Max"] = Vec(mx) },
            ["Position"] = OSD.FromBinary(U16(pos)),
            ["TriangleList"] = OSD.FromBinary(U16(new ushort[] { 0, 1, 2 })),
        };
        return Compress(new OSDArray { map });
    }

    // The "physics_convex" block: HullList holds each hull's point count, Positions all the points, and
    // BoundingVerts the eight corners of the box around them.
    private static byte[] ConvexBlock(Vector3[][] hulls)
    {
        (Vector3 mn, Vector3 mx) = Bounds(hulls.SelectMany(h => h));
        var pos = new List<ushort>();
        foreach (Vector3 p in hulls.SelectMany(h => h))
        {
            pos.Add(Quantize(p.X, mn.X, mx.X));
            pos.Add(Quantize(p.Y, mn.Y, mx.Y));
            pos.Add(Quantize(p.Z, mn.Z, mx.Z));
        }
        var bounding = new List<ushort>();
        for (int i = 0; i < 8; i++)
        {
            bounding.Add(Quantize((i & 1) == 0 ? mn.X : mx.X, mn.X, mx.X));
            bounding.Add(Quantize((i & 2) == 0 ? mn.Y : mx.Y, mn.Y, mx.Y));
            bounding.Add(Quantize((i & 4) == 0 ? mn.Z : mx.Z, mn.Z, mx.Z));
        }
        var map = new OSDMap
        {
            ["Min"] = Vec(mn),
            ["Max"] = Vec(mx),
            ["HullList"] = OSD.FromBinary(hulls.Select(h => (byte)h.Length).ToArray()),
            ["Positions"] = OSD.FromBinary(U16(pos)),
            ["BoundingVerts"] = OSD.FromBinary(U16(bounding)),
        };
        return Compress(map);
    }

    /// <summary>A mesh asset: the display mesh as medium LOD, and a "physics_convex" block when hulls are given.</summary>
    internal static byte[] Asset(Vector3[][]? hulls)
    {
        var blocks = new List<(string name, byte[] data)> { ("medium_lod", MeshBlock()) };
        if (hulls != null)
            blocks.Add(("physics_convex", ConvexBlock(hulls)));

        var header = new OSDMap();
        int offset = 0;
        foreach ((string name, byte[] data) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = offset, ["size"] = data.Length };
            offset += data.Length;
        }
        using var ms = new MemoryStream();
        byte[] head = OSDParser.SerializeLLSDBinary(header, true);
        ms.Write(head, 0, head.Length);
        foreach ((_, byte[] data) in blocks)
            ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static PrimitiveBaseShape MeshPbs(byte[] asset)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random(); // the mesher's cache is keyed by it
        pbs.SculptData = asset;
        return pbs;
    }
}
