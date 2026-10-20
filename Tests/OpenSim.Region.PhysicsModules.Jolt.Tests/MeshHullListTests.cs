/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical mesh prim whose shape type is PRIM collides as the hull list its asset carries (one convex hull per entry,
/// at the prim's scale), as Second Life does; identical mesh prims share one cooked shape. A hollow object (a trough, a
/// gateway) built from hulls lets a ball rest on its floor and a box through its opening. Each result is a line
/// "MESH-HULLS ..." in the test output. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MeshHullListTests
{
    private readonly ITestOutputHelper _out;
    public MeshHullListTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;
    private const string ListKind = "hulls(asset";

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    // "hulls(asset x3)" -> 3; 0 for any other kind.
    private static int HullsIn(PhysicsActor a)
    {
        string kind = KindOf(a);
        if (!kind.StartsWith(ListKind + " x", StringComparison.Ordinal) || !kind.EndsWith(")", StringComparison.Ordinal))
            return 0;
        return int.Parse(kind.Substring(ListKind.Length + 2, kind.Length - ListKind.Length - 3), CultureInfo.InvariantCulture);
    }

    private static readonly (Vector3[] verts, int[] tris) TroughDisplay = Soup(Trough);
    private static readonly (Vector3[] verts, int[] tris) GatewayDisplay = Soup(Gateway);

    private static byte[] TroughHulls() => Asset(TroughDisplay, Trough, false);
    private static byte[] GatewayHulls() => Asset(GatewayDisplay, Gateway, false);
    private static byte[] TroughNoHulls() => Asset(TroughDisplay, Trough, true);

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    // The mesher empties a shape's asset bytes once it has decoded them, so a prim that is cooked again at another size
    // fetches its asset again (as it does on a grid). This delegate answers at once.
    private static void Serve(JoltScene scene, UUID id, byte[] asset)
    {
        scene.RequestAssetMethod = (assetId, callback) =>
            callback(assetId == id ? new AssetBase(id, "mesh", (sbyte)AssetType.Mesh, UUID.Zero.ToString()) { Data = asset } : null);
    }

    private static PhysicsActor AddHullMesh(JoltScene scene, byte[] asset, UUID id, float height, bool physical, byte shapeType, Vector3? size = null)
    {
        PrimitiveBaseShape pbs = MeshPbs(asset);
        pbs.SculptTexture = id;
        return scene.AddPrimShape("mesh", pbs, new Vector3(CentreX, CentreY, Ground + height * 0.5f), size ?? Vector3.One, Quaternion.Identity, physical, false, shapeType, _id++);
    }

    // ------------------------------------------------------------------ 1: the hull list, through every change

    [Fact]
    public void A_physical_prim_mesh_with_a_hull_list_cooks_as_its_hulls()
    {
        string scratch = Scratch();
        JoltScene s1 = NewScene(scratch);
        float rest;
        string kind;
        try
        {
            PhysicsActor a = AddMesh(s1, TroughHulls(), TroughHeight, true, Prim, out kind);
            Step(s1, 2f);
            Assert.Equal(3, HullsIn(a));
            rest = DropBall(s1, TroughHeight);
        }
        finally { s1.Dispose(); }

        JoltScene s2 = NewScene(scratch);
        float boxPast;
        try
        {
            AddMesh(s2, GatewayHulls(), GatewayHeight, true, Prim, out _);
            Step(s2, 2f);
            boxPast = FireBox(s2);
        }
        finally { s2.Dispose(); }

        JoltScene s3 = NewScene(scratch);
        float avPast;
        try
        {
            AddMesh(s3, GatewayHulls(), GatewayHeight, true, Prim, out _);
            Step(s3, 2f);
            avPast = WalkAvatar(s3);
        }
        finally { s3.Dispose(); }

        Say($"MESH-HULLS physical PRIM hull list: kind={kind} ball_rest_z={F(rest)} box_past_far_side={F(boxPast)} avatar_past_far_side={F(avPast)}");
        Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);   // on the inner floor, not on a hull over the top
        Assert.True(boxPast > 0f, $"the box did not pass the opening: {boxPast}");
        Assert.True(avPast > 0f, $"the avatar did not pass the opening: {avPast}");
    }

    [Fact]
    public void The_hull_list_follows_a_scale_change_a_physical_switch_and_a_shape_type_change()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            Serve(scene, id, asset);
            PhysicsActor a = AddHullMesh(scene, asset, id, TroughHeight, true, Prim);
            Step(scene, 2f);
            Assert.Equal(3, HullsIn(a));

            // Twice the size: the hulls are cooked again at the new scale, so the floor is twice as high.
            a.Size = new Vector3(2f, 2f, 2f);
            a.Position = new Vector3(CentreX, CentreY, Ground + TroughHeight + 0.1f);
            Step(scene, 3f);
            Assert.Equal(3, HullsIn(a));
            float rest = DropBall(scene, TroughHeight * 2f);
            Say($"MESH-HULLS scaled x2: ball_rest_z={F(rest)} (floor top {F(TroughFloorTop * 2f)})");
            Assert.InRange(rest, TroughFloorTop * 2f + 0.1f, TroughFloorTop * 2f + 0.3f);

            // Not physical: the triangle mesh. Physical again: the hulls.
            a.IsPhysical = false;
            Assert.Equal("mesh(mesher)", KindOf(a));
            a.IsPhysical = true;
            Assert.Equal(3, HullsIn(a));

            // CONVEX is one hull of every point; PRIM is the hull list again.
            a.PhysicsShapeType = Convex;
            Assert.Equal("hull(mesher)", KindOf(a));
            a.PhysicsShapeType = Prim;
            Assert.Equal(3, HullsIn(a));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_linkset_child_that_is_a_physical_mesh_cooks_as_its_hulls()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            // A small physical box as the root, beside a physical trough linked to it. The trough sits on the ground.
            Vector3 rootAt = new(CentreX + 4f, CentreY, Ground + 0.25f);
            PhysicsActor root = scene.AddPrimShape("root", PrimitiveBaseShape.CreateBox(), rootAt, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            Serve(scene, id, asset);
            PhysicsActor child = AddHullMesh(scene, asset, id, TroughHeight, true, Prim);
            child.link(root);
            Step(scene, 3f);
            Assert.Equal(3, HullsIn(child));

            float rest = DropBall(scene, TroughHeight);
            Say($"MESH-HULLS linkset child: kind={KindOf(child)} ball_rest_z={F(rest)}");
            Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);

            // Resized while linked: cooked again at the new scale, still as hulls.
            child.Size = new Vector3(1.5f, 1.5f, 1.5f);
            Step(scene, 2f);
            Assert.Equal(3, HullsIn(child));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: each mesh has its own hulls (the mesher's per-mesh list)

    [Fact]
    public void Two_different_meshes_each_get_their_own_hulls_in_any_order_and_from_the_mesher_cache()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] threeAsset = GatewayHulls();                                                   // 3 hulls
            byte[] twoAsset = Asset(Soup(Trough.Take(2).ToArray()), Trough.Take(2).ToArray(), false);   // 2 hulls
            UUID threeId = UUID.Random(), twoId = UUID.Random();
            // A prim of each asset: the shape of its own (the mesher empties the bytes it has decoded), the same sculpt id.
            PrimitiveBaseShape three() => Copy(threeAsset, threeId);
            PrimitiveBaseShape two() => Copy(twoAsset, twoId);
            Vector3 at(int i) => new(10f + i * 6f, 10f, Ground + 5f);

            // Non-physical first: the mesher decodes each asset once and keeps both. Then physical: it answers from its cache
            // (the same texture and size), and the second asset was the one decoded last.
            PhysicsActor a0 = scene.AddPrimShape("a", three(), at(0), Vector3.One, Quaternion.Identity, false, false, Prim, _id++);
            PhysicsActor b0 = scene.AddPrimShape("b", two(), at(1), Vector3.One, Quaternion.Identity, false, false, Prim, _id++);
            PhysicsActor a1 = scene.AddPrimShape("a physical", three(), at(2), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor b1 = scene.AddPrimShape("b physical", two(), at(3), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor a2 = scene.AddPrimShape("a physical again", three(), at(4), new Vector3(1.5f, 1.5f, 1.5f), Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor b2 = scene.AddPrimShape("b physical again", two(), at(5), new Vector3(1.5f, 1.5f, 1.5f), Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor a3 = scene.AddPrimShape("a physical third", three(), at(6), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);

            Say($"MESH-HULLS two meshes: a={HullsIn(a1)},{HullsIn(a2)},{HullsIn(a3)} b={HullsIn(b1)},{HullsIn(b2)}");
            Assert.Equal(new[] { 3, 3, 3 }, new[] { HullsIn(a1), HullsIn(a2), HullsIn(a3) });
            Assert.Equal(new[] { 2, 2 }, new[] { HullsIn(b1), HullsIn(b2) });
            Assert.Equal("mesh(mesher)", KindOf(a0));
            Assert.Equal("mesh(mesher)", KindOf(b0));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: the fallbacks, each logged once per asset

    // Five points on one line: Jolt's hull builder cannot make a hull of them.
    private static Vector3[] Collinear() => new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f) };

    private static Vector3[][] Hulls(params Vector3[][] hulls) => hulls;

    [Fact]
    public void A_hull_with_fewer_than_four_points_or_one_Jolt_cannot_build_is_skipped_and_logged_once()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Vector3[] short3 = { new(-1f, -1f, -1f), new(1f, 1f, 1f), new(0f, 1f, 0f) };   // three points
            byte[] asset = AssetWithHulls(TroughDisplay, Hulls(BoxHull(Trough[0]), short3, Collinear(), BoxHull(Trough[1]), BoxHull(Trough[2])));
            UUID id = UUID.Random();
            var kinds = new List<string>();
            var prims = new List<PhysicsActor>();
            for (int i = 0; i < 4; i++)
            {
                PrimitiveBaseShape pbs = MeshPbs(asset);
                pbs.SculptTexture = id;
                PhysicsActor a = scene.AddPrimShape("m", pbs, new Vector3(10f + i * 6f, 10f, Ground + 5f), new Vector3(1f + i * 0.25f, 1f, 1f), Quaternion.Identity, true, false, Prim, _id++);
                prims.Add(a);
                kinds.Add(KindOf(a));
            }
            Say($"MESH-HULLS skipped hulls: kinds={string.Join(" | ", kinds)} notices={string.Join(" || ", scene.HullNotices)}");
            Assert.All(prims, p => Assert.Equal(3, HullsIn(p)));   // the three good hulls
            Assert.Single(scene.HullNotices);                      // four sizes of one asset: one line
            Assert.Contains("2 of 5", scene.HullNotices[0]);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_asset_whose_hulls_are_all_unusable_is_one_hull_of_its_points_and_logged_once()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Vector3[] tiny = { new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f) };
            byte[] asset = AssetWithHulls(TroughDisplay, Hulls(tiny, Collinear()));
            UUID id = UUID.Random();
            var kinds = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                PrimitiveBaseShape pbs = MeshPbs(asset);
                pbs.SculptTexture = id;
                PhysicsActor a = scene.AddPrimShape("m", pbs, new Vector3(10f + i * 6f, 10f, Ground + 5f), new Vector3(1f + i * 0.25f, 1f, 1f), Quaternion.Identity, true, false, Prim, _id++);
                kinds.Add(KindOf(a));
            }
            Say($"MESH-HULLS no usable hull: kinds={string.Join(" | ", kinds)} notices={string.Join(" || ", scene.HullNotices)}");
            Assert.All(kinds, k => Assert.Equal("hull(mesher)", k));
            Assert.Single(scene.HullNotices);
            Assert.Contains("one hull of the points", scene.HullNotices[0]);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_asset_with_no_hull_list_is_one_hull_of_its_points_and_logs_nothing()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            PhysicsActor a = AddMesh(scene, TroughNoHulls(), TroughHeight, true, Prim, out string kind);
            Assert.Equal("hull(mesher)", kind);
            Assert.Empty(scene.HullNotices);
            Assert.NotNull(a);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_asset_over_the_hull_count_limit_is_one_hull_of_its_points_and_logged_once()
    {
        // The viewer's upload allows at most 256 hulls, of at most 256 points each (the decomposition's limits).
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            var boxes = new List<Box>();
            for (int i = 0; i < JoltScene.MaxAssetHulls + 1; i++)
            {
                float x = (i % 16) * 0.2f - 1.6f, y = (i / 16) * 0.2f - 1.6f;
                boxes.Add(new Box(x, x + 0.1f, y, y + 0.1f, -0.1f, 0.1f));
            }
            byte[] asset = Asset(Soup(boxes.ToArray()), boxes.ToArray(), false);
            UUID id = UUID.Random();
            var kinds = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                PrimitiveBaseShape pbs = MeshPbs(asset);
                pbs.SculptTexture = id;
                PhysicsActor a = scene.AddPrimShape("m", pbs, new Vector3(10f + i * 6f, 10f, Ground + 5f), new Vector3(1f + i * 0.25f, 1f, 1f), Quaternion.Identity, true, false, Prim, _id++);
                kinds.Add(KindOf(a));
            }
            Say($"MESH-HULLS over the limit: kinds={string.Join(" | ", kinds)} notices={string.Join(" || ", scene.HullNotices)}");
            Assert.All(kinds, k => Assert.Equal("hull(mesher)", k));
            Assert.Single(scene.HullNotices);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_hull_list_at_the_limits_is_accepted_and_one_over_either_is_not()
    {
        List<List<Vector3>> List(int hulls, int points)
        {
            var l = new List<List<Vector3>>();
            for (int h = 0; h < hulls; h++)
                l.Add(Enumerable.Repeat(Vector3.Zero, points).ToList());
            return l;
        }
        Assert.Equal(256, JoltScene.MaxAssetHulls);
        Assert.Equal(256, JoltScene.MaxPointsPerAssetHull);
        Assert.True(JoltScene.HullListWithinLimits(List(256, 256)));
        Assert.False(JoltScene.HullListWithinLimits(List(257, 8)));
        Assert.False(JoltScene.HullListWithinLimits(List(3, 257)));
    }

    // ------------------------------------------------------------------ 2 (shared shapes): one cook for identical prims

    private static PrimitiveBaseShape Copy(byte[] asset, UUID id)
    {
        PrimitiveBaseShape pbs = MeshPbs(asset);
        pbs.SculptTexture = id;
        return pbs;
    }

    [Theory]
    [InlineData(true, Prim, "hulls(asset x3)")]
    [InlineData(true, Convex, "hull(mesher)")]
    [InlineData(false, Prim, "mesh(mesher)")]
    public void Fifty_identical_mesh_prims_share_one_cooked_shape(bool physical, byte shapeType, string kind)
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            int shapesBefore = scene.CapacityStats().LiveShapeCount;
            var prims = new List<PhysicsActor>();
            for (int i = 0; i < 50; i++)
                prims.Add(scene.AddPrimShape("copy", Copy(asset, id), new Vector3(10f + (i % 10) * 6f, 10f + (i / 10) * 6f, Ground + 5f), Vector3.One,
                                             Quaternion.Identity, physical, false, shapeType, _id++));
            int shapes = scene.CapacityStats().LiveShapeCount - shapesBefore;
            Say($"MESH-HULLS 50 copies physical={physical} shape_type={shapeType}: cooks={scene.MeshShapesCooked} shapes_held={shapes} kind={KindOf(prims[0])}");
            Assert.All(prims, p => Assert.StartsWith(kind.Substring(0, kind.IndexOf('(') + 1), KindOf(p)));
            Assert.Equal(1, scene.MeshShapesCooked);
            Assert.Equal(1, shapes);
            Assert.Equal(1, scene.SharedShapeCount);
            Assert.All(prims, p => Assert.Equal(((JoltPrim)prims[0]).ShapeHandle, ((JoltPrim)p).ShapeHandle));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_different_size_physical_state_or_shape_type_is_a_different_shape()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            PhysicsActor Add(Vector3 size, bool physical, byte type, int i)
                => scene.AddPrimShape("copy", Copy(asset, id), new Vector3(10f + i * 6f, 10f, Ground + 5f), size, Quaternion.Identity, physical, false, type, _id++);
            Add(Vector3.One, true, Prim, 0);
            Assert.Equal(1, scene.MeshShapesCooked);
            Add(Vector3.One, true, Prim, 1);
            Assert.Equal(1, scene.MeshShapesCooked);
            Add(new Vector3(1f, 1f, 1.5f), true, Prim, 2);   // size: the same cook, scaled (MeshResizeScaledShapeTests)
            Assert.Equal(1, scene.MeshShapesCooked);
            Assert.Equal(1, scene.ScaledShapesMade);
            Add(Vector3.One, false, Prim, 3);                // physical state
            Assert.Equal(2, scene.MeshShapesCooked);
            Add(Vector3.One, true, Convex, 4);               // shape type
            Assert.Equal(3, scene.MeshShapesCooked);
            Assert.Equal(4, scene.SharedShapeCount);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_shared_shape_is_freed_when_the_last_body_using_it_is_removed()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            int shapesBefore = scene.CapacityStats().LiveShapeCount;
            var prims = new List<PhysicsActor>();
            for (int i = 0; i < 50; i++)
                prims.Add(scene.AddPrimShape("copy", Copy(asset, id), new Vector3(10f + (i % 10) * 6f, 10f + (i / 10) * 6f, Ground + 5f), Vector3.One,
                                             Quaternion.Identity, true, false, Prim, _id++));
            for (int i = 0; i < 49; i++)
                scene.RemovePrim(prims[i]);
            Assert.Equal(shapesBefore + 1, scene.CapacityStats().LiveShapeCount);   // one body still uses it
            Assert.Equal(1, scene.SharedShapeCount);
            Step(scene, 1f);                                                       // and it still stands
            scene.RemovePrim(prims[49]);
            Say($"MESH-HULLS freed: shapes before={shapesBefore} after the last removal={scene.CapacityStats().LiveShapeCount} shared={scene.SharedShapeCount}");
            Assert.Equal(shapesBefore, scene.CapacityStats().LiveShapeCount);
            Assert.Equal(0, scene.SharedShapeCount);

            // The same mesh again is cooked again (nothing stale is held).
            scene.AddPrimShape("again", Copy(asset, id), new Vector3(10f, 10f, Ground + 5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            Assert.Equal(2, scene.MeshShapesCooked);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Resizing_one_of_several_copies_leaves_the_others_on_the_shared_shape_and_counts_no_leak()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            Serve(scene, id, asset);
            int shapesBefore = scene.CapacityStats().LiveShapeCount;
            var prims = new List<PhysicsActor>();
            for (int i = 0; i < 5; i++)
                prims.Add(scene.AddPrimShape("copy", Copy(asset, id), new Vector3(10f + i * 6f, 10f, Ground + 5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++));
            for (int round = 0; round < 20; round++)
                prims[0].Size = round % 2 == 0 ? new Vector3(1.5f, 1f, 1f) : Vector3.One;
            // prims[0] ended at size one again: back on the shared shape
            Assert.Equal(((JoltPrim)prims[1]).ShapeHandle, ((JoltPrim)prims[0]).ShapeHandle);
            // Size one is cooked once; size 1.5 is a scaled shape over it, made each time the copy goes to it, because a shape
            // nobody uses is freed.
            Assert.Equal(1, scene.MeshShapesCooked);
            Assert.InRange(scene.ScaledShapesMade, 1, 10);
            foreach (PhysicsActor p in prims) scene.RemovePrim(p);
            Assert.Equal(shapesBefore, scene.CapacityStats().LiveShapeCount);
            Assert.Equal(0, scene.SharedShapeCount);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_copy_that_joins_a_linkset_leaves_the_shape_the_others_share_alone()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughHulls();
            UUID id = UUID.Random();
            PhysicsActor other = scene.AddPrimShape("other", Copy(asset, id), new Vector3(10f, 10f, Ground + 5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor part = scene.AddPrimShape("part", Copy(asset, id), new Vector3(30f, 10f, Ground + 5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            PhysicsActor root = scene.AddPrimShape("root", PrimitiveBaseShape.CreateBox(), new Vector3(31f, 10f, Ground + 5f), new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
            Assert.Equal(((JoltPrim)other).ShapeHandle, ((JoltPrim)part).ShapeHandle);
            float massBefore = other.Mass;

            part.Density = 3000f;   // a part's own density is given to its shape in the linkset's compound
            part.link(root);
            Step(scene, 2f);

            Assert.NotEqual(((JoltPrim)other).ShapeHandle, ((JoltPrim)part).ShapeHandle);   // the part took a shape of its own
            Assert.Equal(massBefore, other.Mass, 3);
            Assert.Equal(3, HullsIn(part));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Disposing_the_scene_drops_the_shared_shapes()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        byte[] asset = TroughHulls();
        UUID id = UUID.Random();
        for (int i = 0; i < 5; i++)
            scene.AddPrimShape("copy", Copy(asset, id), new Vector3(10f + i * 6f, 10f, Ground + 5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
        Assert.Equal(1, scene.SharedShapeCount);
        scene.Dispose();
        Assert.Equal(0, scene.SharedShapeCount);
    }
}
