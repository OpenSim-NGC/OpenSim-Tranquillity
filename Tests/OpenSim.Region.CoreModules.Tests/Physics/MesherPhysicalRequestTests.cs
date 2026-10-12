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

namespace OpenSim.Region.CoreModules.Tests.Physics;

/// <summary>
/// A non-physical request for a mesh whose every dimension is under 0.2 m is answered with a bounding box; a
/// physical request gets the mesh itself. The mesher must honour the caller's physical flag through every
/// CreateMesh overload and keep the two answers apart in its cache.
/// </summary>
public class MesherPhysicalRequestTests
{
    private const float Lod = 32f;
    private static readonly Vector3 Small = new Vector3(0.1f, 0.1f, 0.1f);
    private static readonly Vector3 Large = new Vector3(2f, 3f, 4f);

    private static readonly Vector3[][] ThreeHulls = MesherHullListTests.Boxes((0f, 0f, 0f, 1f, 1f, 1f), (2f, 0f, 0f, 3f, 1f, 1f), (0f, 2f, 0f, 1f, 3f, 2f));
    private static readonly Vector3[][] OneHull = MesherHullListTests.Boxes((-1f, -1f, -1f, 0.5f, 0.25f, 0.75f));

    /// <summary>
    /// One mesh asset. The mesher drops the asset bytes from a shape once it has decoded it, so each request is
    /// made with a fresh shape that carries the same asset and the same texture id (which keys the mesher's cache),
    /// as a caller that fetches the asset again would.
    /// </summary>
    private sealed class MeshAsset
    {
        private readonly byte[] m_data;
        private readonly UUID m_id = UUID.Random();

        public MeshAsset(Vector3[][] hulls)
        {
            m_data = MesherHullListTests.Asset(hulls);
        }

        // A shape whose asset is not loaded: only a cache hit can answer a request made with it.
        public PrimitiveBaseShape ShapeWithoutData()
        {
            PrimitiveBaseShape pbs = MesherHullListTests.MeshPbs(Array.Empty<byte>());
            pbs.SculptTexture = m_id;
            return pbs;
        }

        public PrimitiveBaseShape Shape()
        {
            PrimitiveBaseShape pbs = MesherHullListTests.MeshPbs(m_data);
            pbs.SculptTexture = m_id;
            return pbs;
        }
    }

    // The vertex count of the mesh itself (a large request is never replaced by a box).
    private static int MeshVertexCount(Meshmerizer mesher)
    {
        IMesh real = mesher.CreateMesh("real", new MeshAsset(ThreeHulls).Shape(), Large, Lod, false, false)!;
        return real.getVertexList().Count;
    }

    private static int Vertices(IMesh? mesh)
    {
        Assert.NotNull(mesh);
        return mesh!.getVertexList().Count;
    }

    [Fact]
    public void A_physical_request_for_a_small_mesh_gets_the_mesh_not_the_box()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);

        // The call shape BulletSim uses: physical, not cached, not convex, not for ODE.
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("small", new MeshAsset(ThreeHulls).Shape(), Small, Lod, true, false, false, false)));
    }

    [Fact]
    public void Every_overload_that_takes_the_physical_flag_passes_it_on()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);

        // Five-, six-, seven- and eight-argument forms, each with an asset of its own so none is a cache hit.
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("a", new MeshAsset(ThreeHulls).Shape(), Small, Lod, true)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("b", new MeshAsset(ThreeHulls).Shape(), Small, Lod, true, false)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("c", new MeshAsset(ThreeHulls).Shape(), Small, Lod, true, false, false)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("d", new MeshAsset(ThreeHulls).Shape(), Small, Lod, true, false, false, false)));
    }

    [Fact]
    public void A_non_physical_request_for_a_small_mesh_still_gets_the_box()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);

        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("a", new MeshAsset(ThreeHulls).Shape(), Small, Lod)));
        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("b", new MeshAsset(ThreeHulls).Shape(), Small, Lod, false)));
        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("c", new MeshAsset(ThreeHulls).Shape(), Small, Lod, false, false, false, false)));
    }

    [Fact]
    public void A_large_request_gets_the_mesh_whether_or_not_it_is_physical()
    {
        var mesher = new Meshmerizer();
        var asset = new MeshAsset(ThreeHulls);
        int meshCount = MeshVertexCount(mesher);

        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("a", asset.Shape(), Large, Lod, false, true)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("b", asset.Shape(), Large, Lod, true, true)));
    }

    [Fact]
    public void Physical_then_non_physical_each_gets_its_own_answer_from_the_cache()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);
        var asset = new MeshAsset(ThreeHulls);

        // The first two decode the asset; the last two are cache hits.
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("1", asset.Shape(), Small, Lod, true, true)));
        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("2", asset.Shape(), Small, Lod, false, true)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("3", asset.ShapeWithoutData(), Small, Lod, true, true)));
        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("4", asset.ShapeWithoutData(), Small, Lod, false, true)));
    }

    [Fact]
    public void Non_physical_then_physical_each_gets_its_own_answer_from_the_cache()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);
        var asset = new MeshAsset(ThreeHulls);

        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("1", asset.Shape(), Small, Lod, false, true)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("2", asset.Shape(), Small, Lod, true, true)));
        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("3", asset.Shape(), Small, Lod, false, true)));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("4", asset.Shape(), Small, Lod, true, true)));
    }

    [Fact]
    public void A_small_mesh_asked_for_non_physical_then_physical_gets_the_mesh_not_null()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);
        PrimitiveBaseShape shape = new MeshAsset(ThreeHulls).Shape();

        Assert.NotEqual(meshCount, Vertices(mesher.CreateMesh("1", shape, Small, Lod, false, true)));
        Assert.Empty(shape.SculptData);

        // Same shape, whose asset bytes the mesher dropped after the first decode.
        IMesh? physical = mesher.CreateMesh("2", shape, Small, Lod, true, true);
        Assert.Equal(meshCount, Vertices(physical));
        Assert.Equal(3, mesher.GetConvexHulls(physical, Small)!.Count);
        Assert.Empty(shape.SculptData);

        // Both answers now come from the cache.
        Assert.NotNull(mesher.CreateMesh("3", shape, Small, Lod, false, true));
        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("4", shape, Small, Lod, true, true)));
    }

    [Fact]
    public void A_small_mesh_asked_for_physical_then_non_physical_gets_the_box_not_null()
    {
        var mesher = new Meshmerizer();
        int meshCount = MeshVertexCount(mesher);
        PrimitiveBaseShape shape = new MeshAsset(ThreeHulls).Shape();

        Assert.Equal(meshCount, Vertices(mesher.CreateMesh("1", shape, Small, Lod, true, true)));
        Assert.Empty(shape.SculptData);

        // Same shape, asset bytes dropped: the box is made from the mesh the first request cached.
        IMesh? box = mesher.CreateMesh("2", shape, Small, Lod, false, true);
        Assert.NotEqual(meshCount, Vertices(box));
        Assert.Equal(3, mesher.GetConvexHulls(box, Small)!.Count);
    }

    [Fact]
    public void The_asset_bytes_kept_for_a_box_are_dropped_once_the_mesh_is_built()
    {
        var mesher = new Meshmerizer();
        PrimitiveBaseShape shape = new MeshAsset(ThreeHulls).Shape();
        ulong key = shape.GetMeshKey(Small, Lod);

        Assert.NotNull(mesher.CreateMesh("1", shape, Small, Lod, false, true));
        Assert.True(KeptSourceCount(key) == 1);

        Assert.NotNull(mesher.CreateMesh("2", shape, Small, Lod, true, true));
        Assert.True(KeptSourceCount(key) == 0);

        // A request that is never replaced by a box keeps nothing.
        PrimitiveBaseShape large = new MeshAsset(ThreeHulls).Shape();
        Assert.NotNull(mesher.CreateMesh("3", large, Large, Lod, false, true));
        Assert.True(KeptSourceCount(large.GetMeshKey(Large, Lod)) == 0);
    }

    [Fact]
    public void Two_threads_asking_for_the_same_small_mesh_get_the_same_box_and_the_cache_holds_it_once()
    {
        AssertSimultaneousRequestsShareOneEntry(Small, false, "m_uniqueBoundingBoxMeshes");
    }

    [Fact]
    public void Two_threads_asking_for_the_same_mesh_get_the_same_mesh_and_the_cache_holds_it_once()
    {
        AssertSimultaneousRequestsShareOneEntry(Large, false, "m_uniqueMeshes");
        AssertSimultaneousRequestsShareOneEntry(Small, true, "m_uniqueMeshes");
    }

    // Each round is a fresh mesh asked for by two threads that start together, each with a shape of its own that
    // carries the asset (as two callers that loaded it would).
    private static void AssertSimultaneousRequestsShareOneEntry(Vector3 size, bool physical, string cacheField)
    {
        const int Rounds = 100;
        var mesher = new Meshmerizer();
        var cache = (Dictionary<ulong, Mesh>)typeof(Meshmerizer).GetField(cacheField, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        for (int round = 0; round < Rounds; round++)
        {
            var asset = new MeshAsset(ThreeHulls);
            PrimitiveBaseShape[] shapes = { asset.Shape(), asset.Shape() };
            var start = new Barrier(2);
            IMesh?[] answers = new IMesh?[2];
            Exception?[] errors = new Exception?[2];

            Thread[] threads = new Thread[2];
            for (int t = 0; t < 2; t++)
            {
                int me = t;
                threads[t] = new Thread(() =>
                {
                    start.SignalAndWait();
                    try { answers[me] = mesher.CreateMesh("race", shapes[me], size, Lod, physical, true); }
                    catch (Exception e) { errors[me] = e; }
                });
                threads[t].Start();
            }
            foreach (Thread thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

            Assert.Null(errors[0]);
            Assert.Null(errors[1]);
            Assert.NotNull(answers[0]);
            Assert.Same(answers[0], answers[1]);

            lock (cache)
            {
                Assert.Single(cache.Where(kv => ReferenceEquals(kv.Value, answers[0])));
                Assert.Same(answers[0], cache[asset.ShapeWithoutData().GetMeshKey(size, Lod)]);
            }
        }
    }

    private static int KeptSourceCount(ulong key)
    {
        var field = typeof(Meshmerizer).GetField("m_boundingBoxSources", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var kept = (Dictionary<ulong, byte[]>)field!.GetValue(null)!;
        lock (kept)
            return kept.ContainsKey(key) ? 1 : 0;
    }

    [Fact]
    public void Hull_list_and_bounding_hull_follow_the_mesh_asked_for_physical_or_not()
    {
        var mesher = new Meshmerizer();
        var a = new MeshAsset(ThreeHulls);
        var b = new MeshAsset(OneHull);

        foreach (bool physical in new[] { true, false, true, false })
        {
            Assert.NotNull(mesher.CreateMesh("a", a.Shape(), Small, Lod, physical, true));
            Assert.Equal(3, mesher.GetConvexHulls(Small)!.Count);
            Assert.Equal(8, mesher.GetBoundingHull(Small)!.Count);

            Assert.NotNull(mesher.CreateMesh("b", b.Shape(), Small, Lod, physical, true));
            Assert.Single(mesher.GetConvexHulls(Small)!);
        }
    }
}
