/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A mesh or sculpt part is never cooked as a native box, sphere or cylinder from its shape fields. A mesh upload writes its
/// face count into those fields (<c>PrimitiveBaseShape.CreateMesh</c>: 6 faces is a square profile on a straight path, 3
/// faces a circle profile on a straight path), so a part of 3 or 6 faces shows no cuts although its shape is whatever the
/// asset holds. Plain prims keep their native shapes. Serial with the other native tests (the native library is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MeshNativeShapeTests
{
    private readonly ITestOutputHelper _out;
    public MeshNativeShapeTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const float Side = 2f;

    private static readonly Box UnitCube = new(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);
    private static readonly (Vector3[] verts, int[] tris) CubeDisplay = Soup(new[] { UnitCube });
    private static readonly (Vector3[] verts, int[] tris) GatewayDisplay = Soup(Gateway);
    private static readonly (Vector3[] verts, int[] tris) TroughDisplay = Soup(Trough);

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    public static IEnumerable<object[]> FaceCounts()
    {
        for (int faces = 1; faces <= 8; faces++)
            yield return new object[] { faces };
    }

    // A part made by the upload path: its fields from the face count, its asset in the SculptData the mesher reads.
    private static PhysicsActor AddUploaded(JoltScene scene, int faces, byte[] asset, Vector3 centre, Vector3 size, bool physical, byte shapeType = Prim)
        => scene.AddPrimShape("uploaded", UploadedMeshPbs(faces, asset), centre, size, Quaternion.Identity, physical, false, shapeType, _id++);

    private static Vector3 CubeCentre => new(CentreX, CentreY, Ground + Side * 0.5f);

    // ------------------------------------------------------------------ a: a static part is the mesher's mesh

    [Theory]
    [MemberData(nameof(FaceCounts))]
    public void A_static_mesh_of_any_face_count_on_a_square_footprint_is_cooked_from_its_asset(int faces)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PhysicsActor a = AddUploaded(scene, faces, Asset(CubeDisplay, new[] { UnitCube }, true), CubeCentre, new Vector3(Side, Side, Side), false);
            Say($"MESH-NATIVE static faces={faces}: kind={KindOf(a)}");
            Assert.Equal("mesh(mesher)", KindOf(a));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ b: a physical part is its asset's hull list

    [Theory]
    [MemberData(nameof(FaceCounts))]
    public void A_physical_mesh_of_any_face_count_cooks_the_hull_list_of_its_asset(int faces)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PhysicsActor a = AddUploaded(scene, faces, Asset(TroughDisplay, Trough, false), new Vector3(CentreX, CentreY, Ground + TroughHeight * 0.5f), Vector3.One, true);
            Step(scene, 1f);
            Say($"MESH-NATIVE physical faces={faces}: kind={KindOf(a)} hulls={((JoltPrim)a).HullCount}");
            Assert.Equal(Trough.Length, ((JoltPrim)a).HullCount);
            Assert.StartsWith("hulls(asset", KindOf(a), StringComparison.Ordinal);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ c: a 6-face mesh with an opening

    [Fact]
    public void A_static_six_face_mesh_with_an_opening_lets_a_ray_and_an_avatar_through()
    {
        string scratch = Scratch();
        Vector3 at = new(CentreX, CentreY, Ground + GatewayHeight * 0.5f);
        byte[] asset = Asset(GatewayDisplay, Gateway, true);

        JoltScene s1 = NewScene(scratch);
        int throughOpening, throughPillar;
        try
        {
            AddUploaded(s1, 6, asset, at, Vector3.One, false);
            Step(s1, 1f);
            // Along y at 1 m above the ground: the middle of the opening (1.6 m wide), then 1.4 m to the side, through a pillar.
            throughOpening = s1.RaycastWorld(new Vector3(CentreX, CentreY - 3f, Ground + 1f), Vector3.UnitY, 6f, 8).Count;
            throughPillar = s1.RaycastWorld(new Vector3(CentreX + 1.4f, CentreY - 3f, Ground + 1f), Vector3.UnitY, 6f, 8).Count;
        }
        finally { s1.Dispose(); }

        JoltScene s2 = NewScene(scratch);
        float avatarPast;
        try
        {
            AddUploaded(s2, 6, asset, at, Vector3.One, false);
            Step(s2, 2f);
            avatarPast = WalkAvatar(s2);
        }
        finally { s2.Dispose(); }

        Say($"MESH-NATIVE six-face gateway: ray_through_opening_hits={throughOpening} ray_through_pillar_hits={throughPillar} avatar_past_far_side={F(avatarPast)}");
        Assert.Equal(0, throughOpening);
        Assert.True(throughPillar > 0, "the ray through the pillar missed: the gateway is not where the test thinks");
        Assert.True(avatarPast > 0f, $"the avatar did not pass the opening: {avatarPast}");
    }

    // ------------------------------------------------------------------ d: a 3-face cube is a cube, not its inscribed cylinder

    [Fact]
    public void A_static_three_face_cube_on_a_square_footprint_is_hit_in_its_corner()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            AddUploaded(scene, 3, Asset(CubeDisplay, new[] { UnitCube }, true), CubeCentre, new Vector3(Side, Side, Side), false);
            Step(scene, 1f);
            // 0.9 m along both axes from the middle of a 2 m cube: inside the cube, 1.27 m from its axis, outside a cylinder of radius 1 m.
            float top = Ground + Side;
            List<ContactResult> corner = scene.RaycastWorld(new Vector3(CentreX + 0.9f, CentreY + 0.9f, top + 3f), -Vector3.UnitZ, 10f, 8);
            Say($"MESH-NATIVE three-face cube corner ray: {string.Join(", ", corner.Select(h => F(h.Pos.Z - Ground)))}");
            Assert.Contains(corner, h => h.Pos.Z > top - 0.05f);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ e: a sculpt with a box's fields

    [Fact]
    public void A_sculpt_with_a_square_profile_and_a_straight_path_is_cooked_from_its_map()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();   // square profile, straight path, no cuts
            pbs.SculptEntry = true;
            pbs.SculptType = (byte)SculptType.Sphere;
            pbs.SculptTexture = UUID.Random();
            pbs.SculptData = SculptFlagsTests.Map(SculptType.Sphere);
            PhysicsActor a = scene.AddPrimShape("sculpt", pbs, new Vector3(CentreX, CentreY, Ground + 3f), new Vector3(Side, Side, Side),
                                                Quaternion.Identity, false, false, Prim, _id++);
            Say($"MESH-NATIVE sculpt with box fields: kind={KindOf(a)}");
            Assert.Equal("mesh(mesher)", KindOf(a));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ f: plain prims keep their native shapes

    [Fact]
    public void Plain_box_sphere_and_cylinder_prims_still_cook_to_the_native_shapes()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var size = new Vector3(Side, Side, Side);
            PrimitiveBaseShape cylinder = PrimitiveBaseShape.CreateBox();
            cylinder.ProfileShape = ProfileShape.Circle;   // circle profile, straight path
            string box = KindOf(scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY, Ground + 1f), size, Quaternion.Identity, false, _id++));
            string sphere = KindOf(scene.AddPrimShape("sphere", PrimitiveBaseShape.CreateSphere(), new Vector3(CentreX + 5f, CentreY, Ground + 1f), size, Quaternion.Identity, false, _id++));
            string cyl = KindOf(scene.AddPrimShape("cylinder", cylinder, new Vector3(CentreX + 10f, CentreY, Ground + 1f), size, Quaternion.Identity, false, _id++));
            Say($"MESH-NATIVE plain prims: {box} {sphere} {cyl}");
            Assert.Equal("box", box);
            Assert.Equal("sphere", sphere);
            Assert.Equal("cylinder", cyl);
        }
        finally { scene.Dispose(); }
    }
}
