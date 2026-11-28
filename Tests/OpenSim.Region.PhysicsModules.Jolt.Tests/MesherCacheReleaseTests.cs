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
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The Meshmerizer keeps every mesh it makes for the life of the process (its CreateMesh overloads on IMesher always
/// cache). Once a prim's shape is cooked, the scene leaves the cached mesh holding only its two flat arrays (the
/// vertices and the triangle indices, which a later prim of the same shape reads) and drops the triangle and vertex
/// objects the mesh was built from. These tests pin that, and that shapes cooked after it are the same shapes: a second
/// prim of the same shape, a resize, a prim made physical, and rays against each.
/// Serial with the other native tests (the native engine and the mesher's cache are process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MesherCacheReleaseTests
{
    private const float Lod = 32f;   // the level of detail the scene asks the mesher for (JoltScene.MeshLod)
    private const byte Prim = (byte)PhysShapeType.prim;

    // The mesher's cache is shared by every test in the process: each test uses sizes of its own so that its prims are
    // meshed by it, not served from an entry another test made.
    private static int _sizeSlot;
    private static float UniqueSide() => 2f + 0.001f * Interlocked.Increment(ref _sizeSlot);

    // A box with a square hole of half its width, straight through along z.
    private static PrimitiveBaseShape HollowBox()
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.HollowShape = HollowShape.Square;
        pbs.ProfileHollow = 25000;   // 0.5 of the profile (50000 is the whole)
        return pbs;
    }

    private static IMesher MesherOf(JoltScene scene)
        => (IMesher)typeof(JoltScene).GetField("m_mesher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;

    // The mesh the mesher holds for this shape and size, as the scene asked for it: a cache hit makes nothing new.
    private static Mesh Cached(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, bool realTriangles)
        => (Mesh)MesherOf(scene).CreateMesh("probe", pbs, size, Lod, realTriangles, false, false);

    private static object Field(Mesh mesh, string name)
        => typeof(Mesh).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mesh);

    private static void AssertNoBuilderObjects(Mesh mesh, int vertexFloats, int indexCount)
    {
        Assert.Null(Field(mesh, "m_triangles"));   // the Triangle list (and through it every Vertex)
        Assert.Null(Field(mesh, "m_vertices"));    // the vertex dictionary
        // What a later prim of the same shape reads is still there, as flat arrays.
        Assert.Equal(vertexFloats, mesh.getVertexListAsFloatLocked().Length);
        Assert.Equal(indexCount, mesh.getIndexListAsIntLocked().Length);
    }

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    private static PhysicsActor AddPrim(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, bool physical, float x = CentreX)
        => scene.AddPrimShape("prim", pbs, new Vector3(x, CentreY, Ground + 5f), size, Quaternion.Identity, physical, false, Prim, _id++);

    // A ray straight down at (x, y) against this prim only: the height it hits, or null for a miss.
    // With backFaces false a triangle met from behind is not hit (a ray from outside an inside-out shape passes it).
    private static float? HitHeight(JoltScene scene, PhysicsActor a, float x, float y, bool backFaces = true)
    {
        RayFilterFlags flags = RayFilterFlags.AllPrims | RayFilterFlags.ClosestHit | (backFaces ? 0 : RayFilterFlags.BackFaceCull);
        List<ContactResult> hits = scene.RaycastActor(a, new Vector3(x, y, Ground + 30f), -Vector3.UnitZ, 40f, 1, flags);
        return hits.Count == 0 ? null : hits[0].Pos.Z;
    }

    // A hollow box collides as a ring: a ray down its hole misses it, a ray down its wall hits its top face.
    private static void AssertCollidesAsHollowBox(JoltScene scene, PhysicsActor a, float x, Vector3 size, string what)
    {
        Assert.True(KindOf(a) == "mesh(mesher)", $"{what}: kind {KindOf(a)}");
        Assert.Null(HitHeight(scene, a, x, CentreY));                          // through the hole
        float? top = HitHeight(scene, a, x + 0.375f * size.X, CentreY);        // the middle of the wall
        Assert.NotNull(top);
        Assert.InRange(top!.Value, Ground + 5f + 0.5f * size.Z - 0.01f, Ground + 5f + 0.5f * size.Z + 0.01f);
    }

    [Fact]
    public void CookMeshShape_HollowBox_MesherEntryKeepsNoBuilderObjects()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PrimitiveBaseShape pbs = HollowBox();
            var size = new Vector3(UniqueSide(), UniqueSide(), 1f);
            // The mesh made before the scene cooks (the scene is then served this same cache entry): its array sizes,
            // read from its builder objects, are what the kept arrays must hold.
            Mesh before = Cached(scene, pbs, size, false);
            int vertexFloats = before.getVertexListAsFloat().Length, indexCount = before.getIndexListAsInt().Length;
            Assert.True(vertexFloats > 0 && indexCount > 0);

            PhysicsActor a = AddPrim(scene, pbs, size, false);
            Assert.Equal("mesh(mesher)", KindOf(a));

            Mesh after = Cached(scene, pbs, size, false);
            Assert.Same(before, after);
            AssertNoBuilderObjects(after, vertexFloats, indexCount);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CookMeshShape_PhysicalHollowBox_MesherEntryKeepsNoBuilderObjects()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PrimitiveBaseShape pbs = HollowBox();
            var size = new Vector3(UniqueSide(), UniqueSide(), 1f);
            PhysicsActor a = AddPrim(scene, pbs, size, true);
            Assert.StartsWith(JoltScene.PrimCellsKind, KindOf(a));

            // A physical prim of shape type PRIM is cooked from its shape parameters at unit size (shared cells), and
            // reads the mesher's mesh at that size.
            Mesh cached = Cached(scene, pbs, Vector3.One, false);
            Assert.Null(Field(cached, "m_triangles"));
            Assert.Null(Field(cached, "m_vertices"));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CookMeshShape_UploadedMesh_MesherEntryKeepsNoBuilderObjects()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            (Vector3[] verts, int[] tris) display = Soup(Trough);
            PrimitiveBaseShape pbs = MeshPbs(Asset(display, Trough, false));
            pbs.SculptTexture = UUID.Random();
            var size = new Vector3(UniqueSide(), 1f, 1f);
            PhysicsActor a = scene.AddPrimShape("mesh", pbs, new Vector3(CentreX, CentreY, Ground + 5f), size, Quaternion.Identity, false, false, Prim, _id++);
            Assert.Equal("mesh(mesher)", KindOf(a));

            Mesh cached = Cached(scene, pbs, size, true);
            Assert.Null(Field(cached, "m_triangles"));
            Assert.Null(Field(cached, "m_vertices"));
            Assert.Equal(display.tris.Length, cached.getIndexListAsIntLocked().Length);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CookMeshShape_IdenticalPrims_ShareOneMesherEntryAndCollideAsTheirShape()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var size = new Vector3(UniqueSide(), UniqueSide(), 1f);
            FieldInfo f = typeof(Meshmerizer).GetField("m_uniqueMeshes", BindingFlags.NonPublic | BindingFlags.Static)!;
            var cache = (System.Collections.IDictionary)f.GetValue(null)!;
            int entries0;
            lock (cache) entries0 = cache.Count;

            PhysicsActor first = AddPrim(scene, HollowBox(), size, false, CentreX - 40f);
            PhysicsActor second = AddPrim(scene, HollowBox(), size, false, CentreX + 40f);
            int entries;
            lock (cache) entries = cache.Count;

            Assert.Equal(1, entries - entries0);   // one mesh for both prims
            AssertCollidesAsHollowBox(scene, first, CentreX - 40f, size, "first prim");
            AssertCollidesAsHollowBox(scene, second, CentreX + 40f, size, "second prim (served from the cache)");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CookMeshShape_ResizedPrim_CollidesAsItsNewSize()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var size = new Vector3(UniqueSide(), UniqueSide(), 1f);
            PhysicsActor a = AddPrim(scene, HollowBox(), size, false);
            AssertCollidesAsHollowBox(scene, a, CentreX, size, "first size");

            var bigger = new Vector3(UniqueSide() + 2f, UniqueSide() + 2f, 2f);
            a.Size = bigger;
            Step(scene, Heartbeat);
            AssertCollidesAsHollowBox(scene, a, CentreX, bigger, "resized");

            a.Size = size;   // back to a size whose mesh the cache already holds, now as flat arrays only
            Step(scene, Heartbeat);
            AssertCollidesAsHollowBox(scene, a, CentreX, size, "back to the first size");
        }
        finally { scene.Dispose(); }
    }

    // The scene turns an inverted sculpt's triangles round before cooking (the physics engine meets a triangle from its
    // front only). It turns its own copy: a second prim of the same sculpt, served the same cached mesh, must be turned
    // from the mesher's order again, not from the first prim's, or it would come out inside out.
    [Fact]
    public void CookMeshShape_TwoInvertedSculptsOfOneMap_AreBothHitFromOutside()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            byte[] map = SculptFlagsTests.Map(SculptType.Sphere);
            const byte InvertedSphere = (byte)SculptType.Sphere | 64;
            PrimitiveBaseShape first = SculptFlagsTests.Sculpt(map, InvertedSphere);
            PrimitiveBaseShape second = SculptFlagsTests.Sculpt(SculptFlagsTests.Map(SculptType.Sphere), InvertedSphere);
            second.SculptTexture = first.SculptTexture;   // one cache entry for both
            var size = new Vector3(UniqueSide(), UniqueSide(), UniqueSide());
            PhysicsActor a = AddPrim(scene, first, size, false, CentreX - 40f);
            PhysicsActor b = AddPrim(scene, second, size, false, CentreX + 40f);
            Assert.Same(Cached(scene, first, size, false), Cached(scene, second, size, false));

            // A ray straight down from above meets the sphere's top, above its centre; an inside-out sphere lets it
            // through to the inside of its bottom, below the centre.
            float centreZ = Ground + 5f;
            float? topA = HitHeight(scene, a, CentreX - 40f, CentreY, backFaces: false);
            float? topB = HitHeight(scene, b, CentreX + 40f, CentreY, backFaces: false);
            Assert.NotNull(topA);
            Assert.NotNull(topB);
            Assert.True(topA!.Value > centreZ + 0.25f * size.Z, $"first sculpt hit at {topA} (centre {centreZ})");
            Assert.True(topB!.Value > centreZ + 0.25f * size.Z, $"second sculpt hit at {topB} (centre {centreZ})");
            Assert.InRange(topB.Value - topA.Value, -0.001f, 0.001f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CookMeshShape_PrimMadePhysicalAfterItsCook_CooksItsCells()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var size = new Vector3(UniqueSide(), UniqueSide(), 1f);
            PhysicsActor a = AddPrim(scene, HollowBox(), size, false);
            Assert.Equal("mesh(mesher)", KindOf(a));

            a.IsPhysical = true;
            Step(scene, Heartbeat);
            Assert.StartsWith(JoltScene.PrimCellsKind, KindOf(a));

            a.IsPhysical = false;
            Step(scene, Heartbeat);
            Assert.Equal("mesh(mesher)", KindOf(a));
        }
        finally { scene.Dispose(); }
    }
}
