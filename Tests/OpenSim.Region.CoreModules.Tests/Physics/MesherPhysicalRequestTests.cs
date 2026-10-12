/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

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
