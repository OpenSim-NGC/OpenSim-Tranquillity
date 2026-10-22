/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Resizing a mesh prim. The shape cooked for the mesh asset is kept, and a prim of another size wraps it in a scaled
/// shape: no asset fetch, no mesher decode and no cook. A shape cooked from scratch at the new size is the reference: the
/// scaled one has to weigh, balance and collide as it does, for a hull list, a triangle mesh and one hull, uniform and not.
/// A size that crosses a size rule (the small-mesh box, the mesher's 0.01 m floor) is cooked as it was. Each result is a
/// line "MESH-RESIZE ..." in the test output. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MeshResizeScaledShapeTests
{
    private readonly ITestOutputHelper _out;
    public MeshResizeScaledShapeTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static readonly (Vector3[] verts, int[] tris) TroughDisplay = Soup(Trough);
    private static readonly (Vector3[] verts, int[] tris) GatewayDisplay = Soup(Gateway);
    private static byte[] TroughAsset() => Asset(TroughDisplay, Trough, false);
    private static byte[] GatewayAsset() => Asset(GatewayDisplay, Gateway, false);

    // The mesher keeps every mesh it decoded, by prim shape and size, for the life of the process. A new entry is one decode.
    private static int MesherDecodes()
    {
        FieldInfo f = typeof(Meshmerizer).GetField("m_uniqueMeshes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cache = (System.Collections.IDictionary)f.GetValue(null)!;
        lock (cache) return cache.Count;
    }

    private sealed class Counts
    {
        public int Fetches;
    }

    // The scene answers each asset request at once, as a grid's asset cache does, and counts them.
    private static Counts Serve(JoltScene scene, UUID id, byte[] asset)
    {
        var counts = new Counts();
        scene.RequestAssetMethod = (assetId, callback) =>
        {
            Interlocked.Increment(ref counts.Fetches);
            callback(assetId == id ? new AssetBase(id, "mesh", (sbyte)AssetType.Mesh, UUID.Zero.ToString()) { Data = asset } : null);
        };
        return counts;
    }

    private static PhysicsActor Add(JoltScene scene, byte[] asset, UUID id, float height, bool physical, byte shapeType, Vector3 size, int slot = 0)
    {
        PrimitiveBaseShape pbs = MeshPbs(asset);
        pbs.SculptTexture = id;
        return scene.AddPrimShape("mesh", pbs, new Vector3(CentreX, CentreY, Ground + height * 0.5f * size.Z + slot * 0f), size, Quaternion.Identity, physical, false, shapeType, _id++);
    }

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    private static Vector3 ResizeStep(int i) => new(1f + 0.05f * i, 1f + 0.03f * i, 1f + 0.04f * i);

    // ---------------------------------------------------------------- 1: what a resize costs

    [Theory]
    [InlineData(true, Prim, "hulls(asset x3)")]     // a hull list
    [InlineData(true, Convex, "hull(mesher)")]      // one hull
    [InlineData(false, Prim, "mesh(mesher)")]       // a triangle mesh
    public void A_prim_resized_twenty_times_fetches_decodes_and_cooks_once(bool physical, byte shapeType, string kind)
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughAsset();
            UUID id = UUID.Random();
            Counts counts = Serve(scene, id, asset);
            int decodes0 = MesherDecodes();
            PhysicsActor a = Add(scene, asset, id, TroughHeight, physical, shapeType, ResizeStep(0));
            int cooks1 = scene.MeshShapesCooked, decodes1 = MesherDecodes() - decodes0;
            for (int i = 1; i <= 20; i++)
            {
                a.Size = ResizeStep(i);
                Step(scene, Heartbeat);
            }
            int cooks = scene.MeshShapesCooked, decodes = MesherDecodes() - decodes0;
            Say($"MESH-RESIZE 20 resizes physical={physical} shape_type={shapeType}: fetches={counts.Fetches} decodes={decodes} cooks={cooks} " +
                $"(first size: decodes={decodes1} cooks={cooks1}) kind={KindOf(a)}");
            Assert.StartsWith(kind.Substring(0, kind.IndexOf('(') + 1), KindOf(a));
            Assert.Equal(0, counts.Fetches);
            Assert.Equal(1, decodes);
            Assert.Equal(1, cooks);
        }
        finally { scene.Dispose(); }
    }

    // ---------------------------------------------------------------- 2: the scaled shape is the shape a fresh cook is

    // A fresh cook at the target size, in a scene of its own, is the reference; the other scene creates the prim at the base
    // size and resizes it. What is compared: weight, the centre of mass, what a ball rests on and what a box and an avatar
    // pass. Tolerances: weight 1 %, centre of mass 2 cm, rest height and distance past the far side 5 cm.
    private const float MassTolerance = 0.01f, CentreTolerance = 0.02f, ReachTolerance = 0.05f;

    // The centre of mass relative to the prim's own position (llGetCenterOfMass less llGetPos).
    private static Vector3 CentreOf(PhysicsActor a) => a.CenterOfMass - a.Position;

    private (float mass, Vector3 centre, float rest) Measure(string scratch, byte[] asset, bool physical, byte shapeType, Vector3 start, Vector3 target, bool resize)
    {
        JoltScene scene = NewScene(scratch);
        try
        {
            UUID id = UUID.Random();
            Serve(scene, id, asset);
            Vector3 first = resize ? start : target;
            PhysicsActor a = Add(scene, asset, id, TroughHeight, physical, shapeType, first);
            Step(scene, 1f);
            if (resize)
            {
                a.Size = target;
                Assert.Equal(1, scene.ScaledShapesMade);   // the new size is the first one's shape, scaled
            }
            a.Position = new Vector3(CentreX, CentreY, Ground + TroughHeight * 0.5f * target.Z + 0.01f);
            Step(scene, 3f);
            float mass = a.Mass;
            Vector3 centre = CentreOf(a);
            float rest = DropBall(scene, TroughHeight * target.Z);
            return (mass, centre, rest);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(true, Prim, 1.7f, 1.7f, 1.7f)]     // hull list, uniform
    [InlineData(true, Prim, 1.5f, 1.0f, 2.0f)]     // hull list, not uniform
    [InlineData(true, Prim, 0.6f, 0.9f, 0.5f)]     // hull list, smaller
    [InlineData(true, Convex, 1.7f, 1.7f, 1.7f)]   // one hull
    [InlineData(true, Convex, 1.5f, 1.0f, 2.0f)]
    [InlineData(false, Prim, 1.7f, 1.7f, 1.7f)]    // triangle mesh
    [InlineData(false, Prim, 1.5f, 1.0f, 2.0f)]
    [InlineData(false, Prim, 0.6f, 0.9f, 0.5f)]
    public void A_resized_prim_weighs_balances_and_collides_as_a_fresh_cook_at_that_size_does(bool physical, byte shapeType, float x, float y, float z)
    {
        string scratch = Scratch();
        byte[] asset = TroughAsset();
        Vector3 start = Vector3.One, target = new(x, y, z);
        (float mass, Vector3 centre, float rest) fresh = Measure(scratch, asset, physical, shapeType, start, target, false);
        (float mass, Vector3 centre, float rest) scaled = Measure(scratch, asset, physical, shapeType, start, target, true);
        Say($"MESH-RESIZE vs fresh physical={physical} shape_type={shapeType} size=({x},{y},{z}): mass {scaled.mass:0.0} vs {fresh.mass:0.0}, " +
            $"centre of mass ({scaled.centre.X:0.000},{scaled.centre.Y:0.000},{scaled.centre.Z:0.000}) vs ({fresh.centre.X:0.000},{fresh.centre.Y:0.000},{fresh.centre.Z:0.000}), " +
            $"ball rests at {scaled.rest:0.000} vs {fresh.rest:0.000}");
        if (fresh.mass > 0.01f)
            Assert.InRange(scaled.mass, fresh.mass * (1f - MassTolerance), fresh.mass * (1f + MassTolerance));
        Assert.InRange(Vector3.Distance(scaled.centre, fresh.centre), 0f, CentreTolerance);
        Assert.InRange(scaled.rest, fresh.rest - ReachTolerance, fresh.rest + ReachTolerance);
    }

    [Theory]
    [InlineData(true, Prim, 1.5f, 1.0f, 2.0f)]
    [InlineData(true, Convex, 1.5f, 1.0f, 2.0f)]
    [InlineData(false, Prim, 1.5f, 1.0f, 2.0f)]
    [InlineData(true, Prim, 1.7f, 1.7f, 1.7f)]
    [InlineData(false, Prim, 0.7f, 0.7f, 0.7f)]
    public void A_resized_gateway_lets_a_box_and_an_avatar_through_as_a_fresh_cook_does(bool physical, byte shapeType, float x, float y, float z)
    {
        string scratch = Scratch();
        byte[] asset = GatewayAsset();
        Vector3 target = new(x, y, z);
        float Run(bool resize, bool avatar)
        {
            JoltScene scene = NewScene(scratch);
            try
            {
                UUID id = UUID.Random();
                Serve(scene, id, asset);
                PhysicsActor a = Add(scene, asset, id, GatewayHeight, physical, shapeType, resize ? Vector3.One : target);
                Step(scene, 1f);
                if (resize)
                    a.Size = target;
                a.Position = new Vector3(CentreX, CentreY, Ground + GatewayHeight * 0.5f * z + 0.01f);
                Step(scene, 2f);
                return avatar ? WalkAvatar(scene) : FireBox(scene);
            }
            finally { scene.Dispose(); }
        }
        float freshBox = Run(false, false), scaledBox = Run(true, false), freshAv = Run(false, true), scaledAv = Run(true, true);
        Say($"MESH-RESIZE gateway physical={physical} shape_type={shapeType} size=({x},{y},{z}): box past far side {scaledBox:0.00} vs {freshBox:0.00}, avatar {scaledAv:0.00} vs {freshAv:0.00}");
        // A gateway scaled in y is deeper or shallower, so "past the far side" is measured from where the far side is: a
        // box that passes in one passes in the other, to within the tolerance.
        Assert.Equal(freshBox > 0f, scaledBox > 0f);
        Assert.Equal(freshAv > 0f, scaledAv > 0f);
        Assert.InRange(scaledBox, freshBox - 0.25f, freshBox + 0.25f);
        Assert.InRange(scaledAv, freshAv - 0.25f, freshAv + 0.25f);
    }

    // ---------------------------------------------------------------- 3: the size rules

    [Fact]
    public void A_size_that_crosses_the_small_mesh_rule_or_the_mesher_floor_is_cooked_as_it_was()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughAsset();
            UUID id = UUID.Random();
            Counts counts = Serve(scene, id, asset);
            PhysicsActor a = Add(scene, asset, id, TroughHeight, false, Prim, Vector3.One);
            Assert.Equal("mesh(mesher)", KindOf(a));

            a.Size = new Vector3(0.04f, 0.04f, 0.04f);          // a non-physical mesh under 0.05 m on every side is a box
            Assert.Equal("box(small mesh)", KindOf(a));

            a.Size = new Vector3(2f, 2f, 2f);                   // and back: a mesh again
            Assert.Equal("mesh(mesher)", KindOf(a));

            // A physical mesh is never boxed for its size, but the mesher does not make a side under 0.01 m: that size is
            // cooked as the mesher makes it, not scaled down from the base.
            a.IsPhysical = true;
            a.Size = new Vector3(2f, 2f, 0.004f);
            string thin = KindOf(a);
            a.Size = new Vector3(2f, 2f, 0.01f);
            string floor = KindOf(a);
            Say($"MESH-RESIZE size rules: fetches={counts.Fetches} thin={thin} at_floor={floor}");
            Assert.StartsWith("hulls(asset", thin);
            Assert.StartsWith("hulls(asset", floor);
        }
        finally { scene.Dispose(); }
    }

    // ---------------------------------------------------------------- 4: shapes are given back

    [Fact]
    public void Twenty_resizes_leave_no_shape_behind()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughAsset();
            UUID id = UUID.Random();
            Serve(scene, id, asset);
            int before = scene.CapacityStats().LiveShapeCount;
            PhysicsActor a = Add(scene, asset, id, TroughHeight, true, Prim, ResizeStep(0));
            PhysicsActor other = Add(scene, asset, id, TroughHeight, true, Prim, ResizeStep(5), 1);
            for (int i = 1; i <= 20; i++)
                a.Size = ResizeStep(i);
            int held = scene.CapacityStats().LiveShapeCount - before;
            Say($"MESH-RESIZE shapes held after 20 resizes of one of two prims: {held}");
            Assert.InRange(held, 1, 4);   // the asset's shape, and one scaled shape for each of the two prims
            scene.RemovePrim(a);
            scene.RemovePrim(other);
            Assert.Equal(before, scene.CapacityStats().LiveShapeCount);
            Assert.Equal(0, scene.SharedShapeCount);
        }
        finally { scene.Dispose(); }
    }

    // ---------------------------------------------------------------- 5: a part of a linkset

    [Fact]
    public void A_linked_mesh_part_resizes_without_a_fetch_and_a_part_with_its_own_density_keeps_a_shape_of_its_own()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            byte[] asset = TroughAsset();
            UUID id = UUID.Random();
            Counts counts = Serve(scene, id, asset);
            PhysicsActor root = scene.AddPrimShape("root", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX + 4f, CentreY, Ground + 0.25f), new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
            PhysicsActor same = Add(scene, asset, id, TroughHeight, true, Prim, Vector3.One);
            PhysicsActor denser = Add(scene, asset, id, TroughHeight, true, Prim, Vector3.One, 1);
            denser.Position = new Vector3(CentreX + 10f, CentreY, Ground + 1f);
            denser.Density = 3000f;
            same.link(root);
            denser.link(root);
            Step(scene, 2f);
            int decodes0 = MesherDecodes();
            int cooks0 = scene.MeshShapesCooked;
            for (int i = 1; i <= 10; i++)
            {
                same.Size = ResizeStep(i);
                Step(scene, Heartbeat);
            }
            Say($"MESH-RESIZE linked part, 10 resizes: fetches={counts.Fetches} decodes={MesherDecodes() - decodes0} cooks={scene.MeshShapesCooked - cooks0}");
            Assert.Equal(0, counts.Fetches);
            Assert.Equal(0, MesherDecodes() - decodes0);
            Assert.Equal(cooks0, scene.MeshShapesCooked);
            Assert.StartsWith("hulls(asset", KindOf(same));

            // The part with its own density never shares a shape (its density is written into it), and its own resize
            // is then a cook like any other, from the mesher's own copy of the mesh.
            Assert.NotEqual(((JoltPrim)same).ShapeHandle, ((JoltPrim)denser).ShapeHandle);
            denser.Size = new Vector3(1.3f, 1.3f, 1.3f);
            Step(scene, 2f);
            Assert.Equal(0, counts.Fetches);
            Assert.StartsWith("hulls(asset", KindOf(denser));
            Assert.True(root.Mass > 0f);
        }
        finally { scene.Dispose(); }
    }
}
