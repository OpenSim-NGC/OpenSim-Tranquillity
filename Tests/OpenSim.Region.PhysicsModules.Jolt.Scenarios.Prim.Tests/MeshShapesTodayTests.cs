/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How a mesh prim with an opening (a gateway, an open trough) collides today. A mesh asset can carry a physics mesh
/// ("physics_mesh"), a convex decomposition ("physics_convex": a hull list and one bounding hull) and its display LODs.
/// The viewer's upload drops the physics mesh when it writes a decomposition. The module cooks what the mesher
/// returns: for a physical prim one convex hull of every point, for a non-physical prim the triangle mesh. These tests
/// record the outcome, with the same shape built from boxes (what the asset's own hull list would give) beside it.
/// Each result is a line "MESH-SHAPES ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public partial class MeshShapesTodayTests
{
    private readonly ITestOutputHelper _out;
    public MeshShapesTodayTests(ITestOutputHelper output) { _out = output; }

    // ---------------------------------------------------------------- what a dropped ball rests on, and what passes

    [Fact]
    public void Trough_and_gateway_today()
    {
        string scratch = Scratch();
        var lines = new List<string>();
        (Vector3[] verts, int[] tris) trough = Soup(Trough), gate = Soup(Gateway);
        foreach (bool withPhysicsMesh in new[] { false, true })
            foreach (bool physical in new[] { false, true })
                foreach (byte type in new byte[] { (byte)PhysShapeType.prim, (byte)PhysShapeType.convex })
                {
                    string asset = withPhysicsMesh ? "physics_mesh" : "hull_list_only";
                    string shape = type == (byte)PhysShapeType.prim ? "PRIM" : "CONVEX";

                    JoltScene s1 = NewScene(scratch);
                    float rest;
                    string kind;
                    try
                    {
                        AddMesh(s1, Asset(trough, Trough, withPhysicsMesh), TroughHeight, physical, type, out kind);
                        Step(s1, 2f);   // a physical trough settles first
                        rest = DropBall(s1, TroughHeight);
                    }
                    finally { s1.Dispose(); }

                    JoltScene s2 = NewScene(scratch);
                    float boxPast, avPast;
                    string kind2;
                    try
                    {
                        AddMesh(s2, Asset(gate, Gateway, withPhysicsMesh), GatewayHeight, physical, type, out kind2);
                        Step(s2, 2f);
                        boxPast = FireBox(s2);
                    }
                    finally { s2.Dispose(); }

                    JoltScene s3 = NewScene(scratch);
                    try
                    {
                        AddMesh(s3, Asset(gate, Gateway, withPhysicsMesh), GatewayHeight, physical, type, out _);
                        Step(s3, 2f);
                        avPast = WalkAvatar(s3);
                    }
                    finally { s3.Dispose(); }

                    lines.Add($"MESH-SHAPES asset={asset} physical={physical} shape={shape} cooked_trough={kind} cooked_gateway={kind2} " +
                              $"ball_rest_z={F(rest)} box_past_far_side={F(boxPast)} avatar_past_far_side={F(avPast)}");

                    // Today: a non-physical mesh keeps its triangle mesh (the ball rests on the trough floor, the box and
                    // the avatar pass the opening); a physical one is one hull of every point (the ball rests on top, the
                    // opening is closed). The shape type and which blocks the asset carries make no difference.
                    if (physical && type == (byte)PhysShapeType.prim && !withPhysicsMesh)
                    {
                        // A physical PRIM mesh whose asset carries a hull list collides as its hulls (MeshHullListTests): the
                        // ball rests on the floor and the opening is open, as for the same shape built of boxes.
                        Assert.StartsWith("hulls(asset", kind);
                        Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);
                        Assert.True(boxPast > 0f && avPast > 0f, $"physical hull-list gateway blocked: box {boxPast}, avatar {avPast}");
                    }
                    else if (physical)
                    {
                        Assert.Equal("hull(mesher)", kind);
                        Assert.InRange(rest, TroughHeight, TroughHeight + 0.4f);
                        Assert.True(boxPast < 0f && avPast < 0f, $"physical gateway let something through: box {boxPast}, avatar {avPast}");
                    }
                    else
                    {
                        Assert.Equal("mesh(mesher)", kind);
                        Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);
                        Assert.True(boxPast > 0f && avPast > 0f, $"non-physical gateway blocked: box {boxPast}, avatar {avPast}");
                    }
                }

        // The hull list's own answer, built from the same boxes: static prims.
        JoltScene r1 = NewScene(scratch);
        float refRest;
        try { AddBoxes(r1, Trough, TroughHeight); refRest = DropBall(r1, TroughHeight); } finally { r1.Dispose(); }
        JoltScene r2 = NewScene(scratch);
        float refBox;
        try { AddBoxes(r2, Gateway, GatewayHeight); refBox = FireBox(r2); } finally { r2.Dispose(); }
        JoltScene r3 = NewScene(scratch);
        float refAv;
        try { AddBoxes(r3, Gateway, GatewayHeight); refAv = WalkAvatar(r3); } finally { r3.Dispose(); }
        lines.Add($"MESH-SHAPES reference=boxes(hull list) ball_rest_z={F(refRest)} box_past_far_side={F(refBox)} avatar_past_far_side={F(refAv)}");
        lines.Add($"MESH-SHAPES expected trough floor top z={F(TroughFloorTop)} ball rest on floor={F(TroughFloorTop + 0.2f)} ball rest on rim/hull top={F(TroughHeight + 0.2f)}");

        foreach (string l in lines) _out.WriteLine(l);
        Console.WriteLine(string.Join("\n", lines));
    }

    // What the module does with a mesh prim whose asset bytes are not loaded (SculptData empty) when the scene has no
    // request-asset delegate to fetch them with: the mesher returns nothing and the prim collides as its bounding box.
    // (With a delegate it asks for the asset and cooks the shape when it arrives: MeshAssetFetchTests.)
    [Fact]
    public void Mesh_prim_without_loaded_asset_collides_as_a_box()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            foreach (bool physical in new[] { false, true })
            {
                PrimitiveBaseShape pbs = MeshPbs(Array.Empty<byte>());
                PhysicsActor a = scene.AddPrimShape("unloaded", pbs, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, Quaternion.Identity,
                                                    physical, false, (byte)PhysShapeType.convex, _id++);
                string kind = (string)typeof(JoltPrim).GetField("_shapeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
                string line = $"MESH-SHAPES asset_not_loaded physical={physical} cooked={kind}";
                _out.WriteLine(line);
                Console.WriteLine(line);
                Assert.Equal("bbox(fallback)", kind);
            }
        }
        finally { scene.Dispose(); }
    }

    // ---------------------------------------------------------------- memory and cook time of identical prims

    private static long NativeBytes()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hundred_identical_bowls(bool physical)
    {
        string scratch = Scratch();
        byte[] asset = Asset(Bowl(40), Trough, true);
        var pbsList = new List<PrimitiveBaseShape>();   // the same asset bytes, one prim each; all carry the same sculpt id
        UUID id = UUID.Random();
        JoltScene scene = NewScene(scratch);
        try
        {
            // Warm: one bowl cooked and removed, so one-time native growth is not counted.
            PhysicsActor warm = scene.AddPrimShape("warm", WithId(asset, id), new Vector3(10f, 10f, Ground + 5f), new Vector3(4f, 4f, 1f), Quaternion.Identity, physical, false, 0, 1u);
            scene.RemovePrim(warm);
            long before = NativeBytes();
            var times = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                var sw = Stopwatch.StartNew();
                scene.AddPrimShape("bowl", WithId(asset, id), new Vector3(20f + (i % 10) * 6f, 20f + (i / 10) * 6f, Ground + 5f), new Vector3(4f, 4f, 1f),
                                   Quaternion.Identity, physical, false, 0, 2000u + (uint)i);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            long after = NativeBytes();
            double first = times[0], rest = times.Skip(1).Average();
            string line = $"MESH-SHAPES copies=100 physical={physical} triangles=3200 growth_kb={(after - before) / 1024} per_copy_kb={(after - before) / 1024 / 100} " +
                          $"first_add_ms={first:0.0} later_adds_mean_ms={rest:0.0} total_ms={times.Sum():0.0}";
            _out.WriteLine(line);
            Console.WriteLine(line);
        }
        finally { scene.Dispose(); }

        static PrimitiveBaseShape WithId(byte[] a, UUID id)
        {
            PrimitiveBaseShape p = MeshPbs(a);
            p.SculptTexture = id;
            return p;
        }
    }

    // The mesher keeps the hull list of the asset it decoded last in fields of its own, and hands it out through
    // GetConvexHulls on the concrete class. A second request for a mesh it already holds is answered from its cache
    // without decoding, so the hulls read after it are those of whatever asset was decoded last.
    [Fact]
    public void Mesher_hull_list_belongs_to_the_last_decode_not_the_last_request()
    {
        string scratch = Scratch();
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        config.Configs["Startup"].Set("DecodedSculptMapPath", scratch);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        PrimitiveBaseShape three = MeshPbs(Asset(Soup(Gateway), Gateway, false));
        PrimitiveBaseShape one = MeshPbs(Asset(Soup(Trough.Take(1).ToArray()), Trough.Take(1).ToArray(), false));
        mesher.CreateMesh("a", three, Vector3.One, 32f, false, false, false);
        int afterThree = mesher.GetConvexHulls(Vector3.One)!.Count;
        mesher.CreateMesh("b", one, Vector3.One, 32f, false, false, false);
        int afterOne = mesher.GetConvexHulls(Vector3.One)!.Count;
        mesher.CreateMesh("a again", three, Vector3.One, 32f, false, false, false);   // answered from the cache
        int afterThreeAgain = mesher.GetConvexHulls(Vector3.One)!.Count;
        string line = $"MESH-SHAPES mesher hull_list_hulls: after asset of 3 = {afterThree}, after asset of 1 = {afterOne}, after asset of 3 again (cache hit) = {afterThreeAgain}";
        _out.WriteLine(line);
        Console.WriteLine(line);
        Assert.Equal(3, afterThree);
        Assert.Equal(1, afterOne);
        Assert.Equal(3, afterThreeAgain);   // the mesher now returns the hulls of the mesh asked for
    }

    // ---------------------------------------------------------------- the mesher's bounding-box substitution

    [Fact]
    public void Small_non_physical_mesh_from_the_mesher()
    {
        string scratch = Scratch();
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        config.Configs["Startup"].Set("DecodedSculptMapPath", scratch);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        byte[] asset = Asset(Soup(Trough), Trough, true);
        foreach (float scale in new[] { 0.1f, 0.19f, 0.25f, 1f })
        {
            IMesh m = mesher.CreateMesh("probe", MeshPbs(asset), new Vector3(scale, scale, scale), 32f, false, false, false);
            int vertices = m.getVertexListAsFloat().Length / 3;
            // The mesher replaces a non-physical mesh with every side under 0.2 m by its bounding box (8 corners); the
            // module asks the mesher for isPhysical = false for every mesh, so this reaches a non-physical or physical prim.
            Assert.Equal(scale < 0.2f, vertices == 8);
            string line = $"MESH-SHAPES mesher scale={scale} isPhysical=false vertices={vertices} triangles={m.getIndexListAsInt().Length / 3}";
            _out.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
