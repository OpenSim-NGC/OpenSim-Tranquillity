/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Physics;

/// <summary>
/// Meshmerizer.TryGetPrimProfileRings hands out the profile and path rings a prim is extruded from. For a set of
/// shapes the rings must reproduce the mesher's own vertices exactly, and describe their layout (centre vertex,
/// outer and hollow ring sizes, the angle of each vertex, the turn and twist of each layer) truthfully.
/// </summary>
public class PrimProfileRingsTests
{
    private const float Lod = 32f;
    private static readonly Vector3 Size = new Vector3(2f, 3f, 1.5f);

    private static readonly MethodInfo CoordsAndFaces =
        typeof(Meshmerizer).GetMethod("GenerateCoordsAndFacesFromPrimShapeData", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // The shapes: ProfileCurve, HollowShape, hollow (0..1), profile cut begin/end (0..1), path curve, path cut begin/end,
    // twist (PathTwist units), path scale / taper
    public sealed record Spec(
        string Name, ProfileShape Profile = ProfileShape.Square, HollowShape Hole = HollowShape.Same, float Hollow = 0f,
        float CutBegin = 0f, float CutEnd = 1f, bool Circular = false, float PathBegin = 0f, float PathEnd = 1f,
        int Twist = 0, int TwistBegin = 0, int Revolutions = 0, byte ScaleX = 100, byte ScaleY = 100, sbyte TaperX = 0,
        sbyte TaperY = 0)
    {
        public override string ToString() => Name;

        public PrimitiveBaseShape Build()
        {
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.PathCurve = (byte)(Circular ? Extrusion.Curve1 : Extrusion.Straight);
            pbs.ProfileShape = Profile;
            pbs.HollowShape = Hole;
            pbs.ProfileBegin = (ushort)MathF.Round(CutBegin * 50000f);
            pbs.ProfileEnd = (ushort)MathF.Round((1f - CutEnd) * 50000f);
            pbs.ProfileHollow = (ushort)MathF.Round(Hollow * 50000f);
            pbs.PathBegin = (ushort)MathF.Round(PathBegin * 50000f);
            pbs.PathEnd = (ushort)MathF.Round((1f - PathEnd) * 50000f);
            pbs.PathTwist = (sbyte)Twist;
            pbs.PathTwistBegin = (sbyte)TwistBegin;
            pbs.PathRevolutions = (byte)Revolutions;
            pbs.PathScaleX = ScaleX;
            pbs.PathScaleY = ScaleY;
            pbs.PathTaperX = TaperX;
            pbs.PathTaperY = TaperY;
            pbs.SculptEntry = false;
            return pbs;
        }
    }

    private static Spec Torus(string name, ProfileShape profile = ProfileShape.Circle, float hollow = 0f) =>
        new Spec(name, profile, Hollow: hollow, Circular: true, ScaleY: 175);

    public static IEnumerable<object[]> Shapes()
    {
        var specs = new List<Spec>
        {
            new("box"),
            new("cylinder", ProfileShape.Circle),
            new("prism", ProfileShape.EquilateralTriangle),
            new("hollow box 0.5", Hollow: 0.5f),
            new("hollow box 0.95", Hollow: 0.95f),
            new("hollow cylinder 0.5", ProfileShape.Circle, Hollow: 0.5f),
            new("hollow cylinder, square hole", ProfileShape.Circle, HollowShape.Square, 0.5f),
            new("hollow box, round hole", ProfileShape.Square, HollowShape.Circle, 0.5f),
            new("hollow prism, triangle hole", ProfileShape.EquilateralTriangle, HollowShape.Triangle, 0.4f),
            new("profile cut cylinder 270", ProfileShape.Circle, CutBegin: 0.25f),
            new("profile cut box 90", CutEnd: 0.25f),
            new("profile cut hollow box", Hollow: 0.5f, CutEnd: 0.75f),
            new("path cut cylinder", ProfileShape.Circle, PathBegin: 0.25f, PathEnd: 0.75f),
            new("twisted box 180", Twist: 100),
            new("twisted hollow cylinder, twist begin", ProfileShape.Circle, Hollow: 0.5f, Twist: 80, TwistBegin: -20),
            new("tapered box", TaperX: 0, ScaleX: 50, ScaleY: 150),
            Torus("torus"),
            Torus("hollow torus", hollow: 0.5f),
            Torus("tube", ProfileShape.Square),
            Torus("hollow tube", ProfileShape.Square, 0.5f),
            Torus("ring", ProfileShape.EquilateralTriangle),
            Torus("torus path cut") with { PathEnd = 0.5f },
            Torus("twisted torus") with { Twist = 50 },
            Torus("two-turn torus") with { Revolutions = 66 },
            new("sphere", ProfileShape.HalfCircle, Circular: true),
            new("hollow sphere", ProfileShape.HalfCircle, Hollow: 0.5f, Circular: true),
            new("bowl", ProfileShape.HalfCircle, Hollow: 0.8f, CutEnd: 0.5f, Circular: true),
        };
        foreach (Spec spec in specs)
            yield return new object[] { spec };
    }

    private static List<Coord> MeshersOwnCoords(Meshmerizer mesher, PrimitiveBaseShape pbs)
    {
        object[] args = { "own", pbs, Size, Lod, null!, null! };
        Assert.True((bool)CoordsAndFaces.Invoke(mesher, args)!);
        return (List<Coord>)args[4];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_rings_reproduce_the_meshers_own_vertices_exactly(Spec spec)
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape pbs = spec.Build();

        List<Coord> own = MeshersOwnCoords(mesher, pbs);
        Assert.True(mesher.TryGetPrimProfileRings(spec.Name, pbs, Size, Lod, out PrimProfileRings? rings));
        Assert.NotNull(rings);

        Assert.Equal(own.Count, rings!.Vertices.Count);
        Assert.Equal(rings.LayerCount * rings.LayerVertexCount, rings.Vertices.Count);
        for (int i = 0; i < own.Count; i++)
        {
            Assert.Equal(own[i].X, rings.Vertices[i].X);
            Assert.Equal(own[i].Y, rings.Vertices[i].Y);
            Assert.Equal(own[i].Z, rings.Vertices[i].Z);
        }

        // every vertex of the mesh the mesher hands out is one of the rings' vertices
        IMesh mesh = mesher.CreateMesh(spec.Name, pbs, Size, Lod)!;
        Assert.NotNull(mesh);
        var ringSet = new HashSet<Vector3>(rings.Vertices);
        foreach (Vector3 v in mesh.getVertexList())
            Assert.Contains(v, ringSet);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_layout_adds_up_and_the_angles_run_the_right_way(Spec spec)
    {
        var mesher = new Meshmerizer();
        Assert.True(mesher.TryGetPrimProfileRings(spec.Name, spec.Build(), Vector3.One, Lod, out PrimProfileRings? r));
        Assert.NotNull(r);

        int centre = r!.HasCentreVertex ? 1 : 0;
        Assert.Equal(centre + r.OuterVertexCount + r.HollowVertexCount, r.LayerVertexCount);
        Assert.Equal(r.LayerVertexCount, r.ProfileAngles.Count);
        Assert.Equal(r.LayerCount, r.LayerTurns.Count);
        Assert.Equal(r.LayerCount, r.LayerTwists.Count);
        Assert.True(r.OuterVertexCount >= 2);
        Assert.Equal(spec.Hollow > 0.001f, r.HollowVertexCount > 0);
        if (r.HollowVertexCount > 0)
            Assert.False(r.HasCentreVertex);

        // the outer ring starts at 0 and does not go back; it turns at most once round
        float previous = r.ProfileAngles[centre];
        Assert.Equal(0f, previous);
        for (int k = 1; k < r.OuterVertexCount; k++)
        {
            float a = r.ProfileAngles[centre + k];
            Assert.True(a >= previous - 1e-5f, $"outer vertex {k}: {a} after {previous}");
            previous = a;
        }
        Assert.InRange(previous, 0.01f, 2f * MathF.PI + 1e-3f);
        float sweep = previous;

        if (r.HollowVertexCount > 0)
        {
            // the hollow ring is stored the other way round: from the last angle back to the first, from the same zero
            int first = centre + r.OuterVertexCount;
            Assert.Equal(0f, r.ProfileAngles[first + r.HollowVertexCount - 1], 1e-4f);
            Assert.Equal(sweep, r.ProfileAngles[first], 1e-4f);
            for (int k = 1; k < r.HollowVertexCount; k++)
                Assert.True(r.ProfileAngles[first + k] <= r.ProfileAngles[first + k - 1] + 1e-5f);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_angles_are_the_directions_of_the_vertices_from_the_centre(Spec spec)
    {
        // On a straight, untwisted, untapered prim at unit size a layer lies flat (x, y), so the angle between two
        // vertices of a ring is the angle of the rays to them from the layer's centre.
        if (spec.Circular || spec.Twist != 0 || spec.TwistBegin != 0 || spec.ScaleX != 100 || spec.ScaleY != 100)
            return;

        var mesher = new Meshmerizer();
        Assert.True(mesher.TryGetPrimProfileRings(spec.Name, spec.Build(), Vector3.One, Lod, out PrimProfileRings? r));
        PrimProfileRings rings = r!;
        int centre = rings.HasCentreVertex ? 1 : 0;

        double Polar(int index)
        {
            Vector3 v = rings.GetVertex(0, index);
            return Math.Atan2(v.Y, v.X);
        }

        double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        double zero = Polar(centre);
        for (int k = 0; k < rings.OuterVertexCount; k++)
            Assert.Equal(0.0, Wrap(Polar(centre + k) - zero - rings.ProfileAngles[centre + k]), 1e-3);

        int first = centre + rings.OuterVertexCount;
        for (int k = 0; k < rings.HollowVertexCount; k++)
            Assert.Equal(0.0, Wrap(Polar(first + k) - zero - rings.ProfileAngles[first + k]), 1e-3);

        // a vertex of the hollow ring and a vertex of the outer ring with the same angle are on one ray from the centre
        for (int h = 0; h < rings.HollowVertexCount; h++)
            for (int o = 0; o < rings.OuterVertexCount; o++)
                if (MathF.Abs(rings.ProfileAngles[first + h] - rings.ProfileAngles[centre + o]) < 1e-4f)
                {
                    Vector3 a = rings.GetVertex(0, first + h), b = rings.GetVertex(0, centre + o);
                    Assert.Equal(0f, a.X * b.Y - a.Y * b.X, 1e-4f);
                    Assert.True(a.X * b.X + a.Y * b.Y > 0f);
                }
    }

    [Fact]
    public void Layout_of_a_box_a_hollow_cylinder_and_a_cut_cylinder()
    {
        var mesher = new Meshmerizer();

        Assert.True(mesher.TryGetPrimProfileRings("box", new Spec("box").Build(), Vector3.One, Lod, out var box));
        Assert.False(box!.HasCentreVertex);
        Assert.Equal(5, box.OuterVertexCount);          // four corners, the first repeated at the end
        Assert.Equal(0, box.HollowVertexCount);
        Assert.Equal(2, box.LayerCount);
        Assert.Equal(2f * MathF.PI, box.ProfileAngles[4], 1e-4f);

        Assert.True(mesher.TryGetPrimProfileRings("tube", new Spec("tube", ProfileShape.Circle, Hollow: 0.5f).Build(), Vector3.One, Lod, out var tube));
        Assert.False(tube!.HasCentreVertex);
        Assert.Equal(25, tube.OuterVertexCount);
        Assert.Equal(25, tube.HollowVertexCount);

        Assert.True(mesher.TryGetPrimProfileRings("pie", new Spec("pie", ProfileShape.Circle, CutBegin: 0.25f).Build(), Vector3.One, Lod, out var pie));
        Assert.True(pie!.HasCentreVertex);
        Assert.Equal(0, pie.HollowVertexCount);
        Assert.Equal(1.5f * MathF.PI, pie.ProfileAngles[pie.OuterVertexCount], 1e-3f);
    }

    [Fact]
    public void A_straight_path_does_not_turn_and_a_circular_path_turns_once_round()
    {
        var mesher = new Meshmerizer();

        Assert.True(mesher.TryGetPrimProfileRings("straight", new Spec("straight", Twist: 50).Build(), Vector3.One, Lod, out var straight));
        Assert.All(straight!.LayerTurns, turn => Assert.Equal(0f, turn));
        Assert.Equal(0f, straight.LayerTwists[0], 1e-6f);
        Assert.Equal(MathF.PI / 2f, straight.LayerTwists[^1], 0.01f);     // PathTwist 50 is 90 degrees on a straight path
        for (int i = 1; i < straight.LayerCount; i++)
            Assert.True(straight.LayerTwists[i] > straight.LayerTwists[i - 1]);

        Assert.True(mesher.TryGetPrimProfileRings("torus", Torus("torus").Build(), Vector3.One, Lod, out var torus));
        Assert.Equal(25, torus!.LayerCount);               // 24 steps, the last on the first one's place
        Assert.Equal(0f, torus.LayerTurns[0], 1e-6f);
        Assert.Equal(2f * MathF.PI, torus.LayerTurns[^1], 1e-4f);
        for (int i = 1; i < torus.LayerCount; i++)
            Assert.Equal(2f * MathF.PI / 24f, torus.LayerTurns[i] - torus.LayerTurns[i - 1], 1e-4f);
        Assert.All(torus.LayerTwists, twist => Assert.Equal(0f, twist));

        // a twisted torus: PathTwist is 3.6 degrees a unit on a circular path; the twist grows with the path
        Assert.True(mesher.TryGetPrimProfileRings("twisted", (Torus("twisted") with { Twist = 50 }).Build(), Vector3.One, Lod, out var twisted));
        Assert.Equal(MathF.PI, twisted!.LayerTwists[^1], 0.01f);
        Assert.True(twisted.LayerCount >= torus.LayerCount);
    }

    [Fact]
    public void A_sculpt_or_mesh_prim_has_no_rings()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Sphere;
        Assert.False(mesher.TryGetPrimProfileRings("sculpt", pbs, Vector3.One, Lod, out PrimProfileRings? rings));
        Assert.Null(rings);

        Assert.False(mesher.TryGetPrimProfileRings("none", null!, Vector3.One, Lod, out rings));
        Assert.Null(rings);
    }
}
