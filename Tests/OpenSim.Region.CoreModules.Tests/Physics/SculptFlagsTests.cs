/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using CoreJ2K;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Physics;

/// <summary>
/// The sculpt type byte holds the map type in its low bits, a flag in 64 (invert) and a flag in 128 (mirror). The
/// viewer draws the type's shape, mirrored when the mirror flag is set and turned inside out when the invert flag is
/// set (llvolume.cpp, LLVolume::sculptGenerateMapVertices): mirror flips x and keeps the faces pointing outwards,
/// invert only reverses the faces, and both together flip x and leave the faces reversed. The mesher has to give the
/// same shapes.
/// </summary>
public class SculptFlagsTests
{
    private const float Lod = 32f;
    private const byte Invert = 64;
    private const byte Mirror = 128;
    private static readonly Vector3 Size = new Vector3(2f, 3f, 4f);

    private static Meshmerizer NewMesher()
    {
        // The decoded-map cache is off so that no folder is needed and every request decodes its own map.
        var source = new IniConfigSource();
        IConfig startup = source.AddConfig("Startup");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("CacheSculptMaps", "false");
        startup.Set("DecodedSculptMapPath", Path.Combine(Path.GetTempPath(), "sculpt-flags-tests-unused"));
        var mesher = new Meshmerizer();
        mesher.Initialise(source);
        return mesher;
    }

    // Offset from the origin so that a mirrored shape is a different shape, not the same one.
    private const float Shift = 0.12f;

    private static SKColor Colour(float x, float y, float z)
    {
        byte B(float v) => (byte)Math.Clamp((int)Math.Round((v + 0.5f) * 255f), 0, 255);
        return new SKColor(B(x + Shift), B(y), B(z));
    }

    // A 64 x 64 sculpt map of the given type: columns run round (or across), rows run down.
    private static byte[] Map(SculptType type)
    {
        const int N = 64;
        using var bmp = new SKBitmap(N, N);
        for (int row = 0; row < N; row++)
            for (int col = 0; col < N; col++)
            {
                double u = col / (double)N;
                double v = row / (double)(N - 1);
                // The direction the columns run round is the one that leaves the unflagged faces pointing outwards.
                double phi = (type == SculptType.Torus ? -2 : 2) * Math.PI * u;
                double theta = Math.PI * v;
                SKColor c = type switch
                {
                    SculptType.Sphere => Colour((float)(0.35 * Math.Sin(theta) * Math.Cos(phi)), (float)(0.3 * Math.Sin(theta) * Math.Sin(phi)), (float)(0.4 * Math.Cos(theta))),
                    SculptType.Torus => Colour((float)((0.28 + 0.15 * Math.Cos(2 * theta)) * Math.Cos(phi)), (float)((0.28 + 0.15 * Math.Cos(2 * theta)) * Math.Sin(phi)), (float)(0.15 * Math.Sin(2 * theta))),
                    SculptType.Cylinder => Colour((float)(0.4 * Math.Cos(phi)), (float)(0.3 * Math.Sin(phi)), (float)(v - 0.5)),
                    _ => Colour((float)(col / (double)(N - 1) - 0.5), (float)(v - 0.5), (float)(0.1 + 0.3 * Math.Sin(Math.PI * col / (N - 1)) * Math.Sin(Math.PI * v))),
                };
                bmp.SetPixel(col, row, c);
            }
        return J2kImage.ToBytes(bmp);
    }

    private static PrimitiveBaseShape Sculpt(byte[] map, byte sculptType, UUID? texture = null)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = sculptType;
        pbs.SculptTexture = texture ?? UUID.Random();
        pbs.SculptData = map;
        return pbs;
    }

    private static IMesh Mesh(Meshmerizer mesher, byte[] map, byte sculptType, bool cache = false, UUID? texture = null)
    {
        IMesh? mesh = mesher.CreateMesh("sculpt", Sculpt(map, sculptType, texture), Size, Lod, true, cache);
        Assert.NotNull(mesh);
        return mesh!;
    }

    private static string P(Vector3 v)
    {
        static string F(float f)
        {
            string s = Math.Round(f, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            return s == "-0.000" ? "0.000" : s;
        }
        return F(v.X) + "," + F(v.Y) + "," + F(v.Z);
    }

    // Each triangle as its three corners, starting at the smallest corner and keeping the winding.
    private static List<string> Triangles(IMesh mesh, bool reflectX = false, bool reverse = false)
    {
        List<Vector3> v = mesh.getVertexList();
        int[] idx = mesh.getIndexListAsInt();
        var result = new List<string>();
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            Vector3 a = v[idx[i]], b = v[idx[i + 1]], c = v[idx[i + 2]];
            if (reflectX)
            {
                a.X = -a.X;
                b.X = -b.X;
                c.X = -c.X;
            }
            if (reverse)
                (b, c) = (c, b);
            string[] corners = { P(a), P(b), P(c) };
            int first = 0;
            for (int k = 1; k < 3; k++)
                if (string.CompareOrdinal(corners[k], corners[first]) < 0)
                    first = k;
            result.Add(corners[first] + "|" + corners[(first + 1) % 3] + "|" + corners[(first + 2) % 3]);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // Six times the volume the surface encloses (positive when the faces point outwards).
    private static double SignedVolume(IMesh mesh)
    {
        List<Vector3> v = mesh.getVertexList();
        int[] idx = mesh.getIndexListAsInt();
        double sum = 0;
        for (int i = 0; i + 2 < idx.Length; i += 3)
            sum += Vector3.Dot(v[idx[i]], Vector3.Cross(v[idx[i + 1]], v[idx[i + 2]]));
        return sum;
    }

    public static IEnumerable<object[]> Types()
    {
        yield return new object[] { SculptType.Sphere };
        yield return new object[] { SculptType.Torus };
        yield return new object[] { SculptType.Plane };
        yield return new object[] { SculptType.Cylinder };
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void The_invert_flag_gives_the_unflagged_shape_with_its_faces_reversed(SculptType type)
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(type);
        IMesh plain = Mesh(mesher, map, (byte)type);
        IMesh inverted = Mesh(mesher, map, (byte)((byte)type | Invert));

        Assert.NotEmpty(plain.getIndexListAsInt());
        Assert.Equal(Triangles(plain, reflectX: false, reverse: true), Triangles(inverted));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void The_mirror_flag_gives_the_unflagged_shape_mirrored_in_x_with_its_faces_still_outward(SculptType type)
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(type);
        IMesh plain = Mesh(mesher, map, (byte)type);
        IMesh mirrored = Mesh(mesher, map, (byte)((byte)type | Mirror));

        // A reflection turns the winding round, so the faces are reversed again to keep them pointing the same way.
        Assert.Equal(Triangles(plain, reflectX: true, reverse: true), Triangles(mirrored));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Both_flags_give_the_mirrored_shape_with_its_faces_reversed(SculptType type)
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(type);
        IMesh plain = Mesh(mesher, map, (byte)type);
        IMesh both = Mesh(mesher, map, (byte)((byte)type | Mirror | Invert));

        Assert.Equal(Triangles(plain, reflectX: true, reverse: false), Triangles(both));
    }

    [Theory]
    [InlineData(SculptType.Sphere)]
    [InlineData(SculptType.Torus)]
    public void A_flagged_closed_sculpt_is_a_solid_shape_that_is_inside_out_only_with_invert(SculptType type)
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(type);

        double plain = SignedVolume(Mesh(mesher, map, (byte)type));
        double inverted = SignedVolume(Mesh(mesher, map, (byte)((byte)type | Invert)));
        double mirrored = SignedVolume(Mesh(mesher, map, (byte)((byte)type | Mirror)));
        double both = SignedVolume(Mesh(mesher, map, (byte)((byte)type | Mirror | Invert)));

        Assert.True(plain > 1.0, "the unflagged sculpt encloses a volume with its faces outwards: " + plain);
        Assert.Equal(-plain, inverted, 3);
        Assert.Equal(plain, mirrored, 3);
        Assert.Equal(-plain, both, 3);
    }

    [Theory]
    [InlineData(SculptType.Sphere)]
    [InlineData(SculptType.Torus)]
    [InlineData(SculptType.Cylinder)]
    public void A_flagged_sculpt_is_not_flat(SculptType type)
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(type);
        foreach (byte flags in new byte[] { Invert, Mirror, (byte)(Invert | Mirror) })
        {
            List<Vector3> v = Mesh(mesher, map, (byte)((byte)type | flags)).getVertexList();
            Assert.True(v.Max(p => p.X) - v.Min(p => p.X) > 0.2f * Size.X, "x extent, flags " + flags);
            Assert.True(v.Max(p => p.Y) - v.Min(p => p.Y) > 0.2f * Size.Y, "y extent, flags " + flags);
            Assert.True(v.Max(p => p.Z) - v.Min(p => p.Z) > 0.2f * Size.Z, "z extent, flags " + flags);
        }
    }

    [Fact]
    public void Two_prims_with_the_same_map_and_size_but_different_flags_each_get_their_own_cached_mesh()
    {
        Meshmerizer mesher = NewMesher();
        byte[] map = Map(SculptType.Sphere);
        UUID texture = UUID.Random();

        IMesh plain = Mesh(mesher, map, (byte)SculptType.Sphere, cache: true, texture: texture);
        IMesh mirrored = Mesh(mesher, map, (byte)SculptType.Sphere | Mirror, cache: true, texture: texture);
        IMesh inverted = Mesh(mesher, map, (byte)SculptType.Sphere | Invert, cache: true, texture: texture);
        IMesh torusFromSameMap = Mesh(mesher, map, (byte)SculptType.Torus, cache: true, texture: texture);

        Assert.NotSame(plain, mirrored);
        Assert.NotSame(plain, inverted);
        Assert.NotSame(plain, torusFromSameMap);
        Assert.Equal(Triangles(plain, reflectX: true, reverse: true), Triangles(mirrored));
        Assert.Equal(Triangles(plain, reverse: true), Triangles(inverted));

        // The same shape asked for again is still answered from the cache.
        Assert.Same(plain, Mesh(mesher, map, (byte)SculptType.Sphere, cache: true, texture: texture));
        Assert.Same(mirrored, Mesh(mesher, map, (byte)SculptType.Sphere | Mirror, cache: true, texture: texture));
    }

    [Fact]
    public void The_mesh_key_tells_sculpts_that_differ_only_in_type_or_flags_apart_and_leaves_other_shapes_alone()
    {
        UUID texture = UUID.Random();
        ulong Key(byte type) => Sculpt(Array.Empty<byte>(), type, texture).GetMeshKey(Size, Lod);

        var keys = new[] { Key(1), Key(2), Key(3), Key(4), Key(1 | Mirror), Key(1 | Invert), Key(1 | Mirror | Invert) };
        Assert.Equal(keys.Length, keys.Distinct().Count());

        // A prim that is not a sculpt does not depend on the sculpt fields.
        PrimitiveBaseShape a = PrimitiveBaseShape.CreateBox();
        PrimitiveBaseShape b = PrimitiveBaseShape.CreateBox();
        b.SculptType = Mirror | Invert | 1;
        Assert.Equal(a.GetMeshKey(Size, Lod), b.GetMeshKey(Size, Lod));
    }

    // Vertex count, triangle count, signed volume and bounds: the values the mesher gave for these maps before the
    // flags were read, so a change to the unflagged path shows up here.
    private static string Fingerprint(IMesh mesh)
    {
        List<Vector3> v = mesh.getVertexList();
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(" ", v.Count, mesh.getIndexListAsInt().Length / 3, SignedVolume(mesh).ToString("F1", ic),
            v.Min(p => p.X).ToString("F2", ic), v.Max(p => p.X).ToString("F2", ic),
            v.Min(p => p.Y).ToString("F2", ic), v.Max(p => p.Y).ToString("F2", ic),
            v.Min(p => p.Z).ToString("F2", ic), v.Max(p => p.Z).ToString("F2", ic));
    }

    [Theory]
    [InlineData(SculptType.Sphere, "989 1976 24.5 -0.45 0.94 -0.89 0.90 -1.61 1.61")]
    [InlineData(SculptType.Torus, "1053 2106 16.9 -0.62 1.00 -1.29 1.30 -0.60 0.62")]
    [InlineData(SculptType.Plane, "1067 2010 -20.6 -0.74 1.00 -1.49 1.50 0.35 1.59")]
    [InlineData(SculptType.Cylinder, "1054 2044 -34.5 -0.55 1.00 -0.90 0.91 -1.98 1.97")]
    public void Sculpts_without_flags_give_the_shapes_the_mesher_gave_before(SculptType type, string expected)
    {
        string actual = Fingerprint(Mesh(NewMesher(), Map(type), (byte)type));
        Assert.True(expected == actual, "expected " + expected + " but the mesh is " + actual);
    }
}
