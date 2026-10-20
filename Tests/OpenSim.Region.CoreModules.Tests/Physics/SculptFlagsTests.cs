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

    // What the mesher gives for an unflagged sculpt: vertex count, triangle count, six times the enclosed volume, and
    // the bounds as min x, max x, min y, max y, min z, max z. The values pin the unflagged mesher path. The sculpt
    // map is a JPEG2000 image, and an image decoder may round a reconstructed sample one level (1 in 255) up or down
    // between versions, which moves vertices by up to one level of the prim size without any change to the mesher. So
    // the counts must match exactly and the volume and the bounds only within a tolerance of that size.
    private sealed record Print(int Vertices, int Triangles, double Volume, double[] Bounds);

    private static readonly System.Globalization.CultureInfo Ic = System.Globalization.CultureInfo.InvariantCulture;

    // Volume: the largest difference seen between two decoders (CoreJ2K 2.3.3.91 and 2.4.1.108, both rounding the same
    // encoded maps) is 0.7, plus 0.05 for the recorded value being rounded to one decimal, so 0.75. Moving every
    // vertex by one level in the worst direction changes it by about 2 x surface area x level, near 1.1 for these
    // shapes (an estimate, not measured). The tolerance is 1.5, which covers that estimate plus the 0.05 for rounding
    // with about 30% to spare. An inverted shape differs by twice the volume, at least 34.8 here.
    private const double VolumeTolerance = 1.5;

    // Bounds: one level of the prim size on that axis (size / 255: x 0.0078, y 0.0118, z 0.0157), plus 0.01 for the
    // recorded value being rounded to two decimals (0.005) with the same again as margin. That is 0.018, 0.022 and
    // 0.026 for x, y and z, against 0.02 at most seen between the two decoders. A mirrored shape's x bounds differ
    // from the unmirrored ones by at least 0.25 here.
    private static double BoundsTolerance(int index)
    {
        float[] size = { Size.X, Size.X, Size.Y, Size.Y, Size.Z, Size.Z };
        return size[index] / 255.0 + 0.01;
    }

    private static Print Fingerprint(IMesh mesh)
    {
        List<Vector3> v = mesh.getVertexList();
        return new Print(v.Count, mesh.getIndexListAsInt().Length / 3, SignedVolume(mesh), new double[]
        {
            v.Min(p => p.X), v.Max(p => p.X), v.Min(p => p.Y), v.Max(p => p.Y), v.Min(p => p.Z), v.Max(p => p.Z),
        });
    }

    private static string Format(Print p) =>
        string.Join(" ", p.Vertices, p.Triangles, p.Volume.ToString("F1", Ic),
            string.Join(" ", p.Bounds.Select(b => b.ToString("F2", Ic))));

    private static Print Parse(string text)
    {
        string[] f = text.Split(' ');
        return new Print(int.Parse(f[0], Ic), int.Parse(f[1], Ic), double.Parse(f[2], Ic),
            f.Skip(3).Select(b => double.Parse(b, Ic)).ToArray());
    }

    private static List<string> Differences(Print expected, Print actual)
    {
        var problems = new List<string>();
        if (expected.Vertices != actual.Vertices)
            problems.Add("vertex count");
        if (expected.Triangles != actual.Triangles)
            problems.Add("triangle count");
        if (Math.Abs(expected.Volume - actual.Volume) > VolumeTolerance)
            problems.Add("volume");
        string[] names = { "min x", "max x", "min y", "max y", "min z", "max z" };
        for (int i = 0; i < names.Length; i++)
            if (Math.Abs(expected.Bounds[i] - actual.Bounds[i]) > BoundsTolerance(i))
                problems.Add(names[i]);
        return problems;
    }

    [Theory]
    [InlineData(SculptType.Sphere, "989 1976 25.1 -0.46 0.94 -0.90 0.90 -1.62 1.61")]
    [InlineData(SculptType.Torus, "1053 2106 17.4 -0.62 1.00 -1.30 1.30 -0.62 0.62")]
    [InlineData(SculptType.Plane, "1067 2010 -20.8 -0.75 1.00 -1.50 1.50 0.35 1.59")]
    [InlineData(SculptType.Cylinder, "1054 2044 -35.2 -0.56 1.00 -0.91 0.91 -2.00 1.97")]
    public void Sculpts_without_flags_give_the_shapes_the_mesher_gave_before(SculptType type, string expected)
    {
        Print actual = Fingerprint(Mesh(NewMesher(), Map(type), (byte)type));
        List<string> problems = Differences(Parse(expected), actual);
        Assert.True(problems.Count == 0,
            "expected " + expected + " but the mesh is " + Format(actual) + " (differs in " + string.Join(", ", problems) + ")");
    }

    [Theory]
    [InlineData("989 1976 25.1 -0.46 0.94 -0.90 0.90 -1.62 1.61")]
    [InlineData("1053 2106 17.4 -0.62 1.00 -1.30 1.30 -0.62 0.62")]
    [InlineData("1067 2010 -20.8 -0.75 1.00 -1.50 1.50 0.35 1.59")]
    [InlineData("1054 2044 -35.2 -0.56 1.00 -0.91 0.91 -2.00 1.97")]
    public void The_fingerprint_comparison_tells_a_mirrored_or_inverted_shape_from_the_unflagged_one(string text)
    {
        Print plain = Parse(text);
        double[] b = plain.Bounds;
        Print mirrored = plain with { Bounds = new[] { -b[1], -b[0], b[2], b[3], b[4], b[5] } };
        Print inverted = plain with { Volume = -plain.Volume };
        Print both = mirrored with { Volume = -plain.Volume };

        Assert.Empty(Differences(plain, plain));
        Assert.Contains("min x", Differences(plain, mirrored));
        Assert.Contains("max x", Differences(plain, mirrored));
        Assert.Equal(new[] { "volume" }, Differences(plain, inverted));
        Assert.Contains("volume", Differences(plain, both));
        Assert.Contains("min x", Differences(plain, both));
    }
}
