/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using CoreJ2K;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A mesh prim whose asset bytes are not loaded asks the scene's request-asset delegate for the asset (as ubODE and
/// BulletSim do) and is cooked to its real shape when the asset arrives. The delegate here is a fake that counts the
/// requests and answers when the test says. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MeshAssetFetchTests
{
    private readonly ITestOutputHelper _out;
    public MeshAssetFetchTests(ITestOutputHelper output) { _out = output; }

    private const string BoxKind = "bbox(fallback)";

    /// <summary>The request-asset delegate: records each request and answers when told to.</summary>
    private sealed class FakeAssets
    {
        private readonly object _gate = new();
        private readonly Dictionary<UUID, List<AssetReceivedDelegate>> _waiting = new();
        public readonly Dictionary<UUID, int> Requests = new();
        public int Total { get { lock (_gate) return Requests.Values.Sum(); } }

        public void Request(UUID id, AssetReceivedDelegate callback)
        {
            lock (_gate)
            {
                Requests[id] = Requests.GetValueOrDefault(id) + 1;
                if (!_waiting.TryGetValue(id, out var list)) _waiting[id] = list = new();
                list.Add(callback);
            }
        }

        public void Answer(UUID id, byte[] data)
        {
            List<AssetReceivedDelegate> list;
            lock (_gate) { _waiting.Remove(id, out list); }
            AssetBase asset = data == null ? null : new AssetBase(id, "mesh", (sbyte)AssetType.Mesh, UUID.Zero.ToString()) { Data = data };
            foreach (var cb in list ?? new()) cb(asset);
        }
    }

    private static JoltScene SceneWith(FakeAssets assets, string scratch, params (string key, string value)[] jolt)
    {
        JoltScene scene = NewScene(scratch, jolt);
        scene.RequestAssetMethod = assets.Request;
        return scene;
    }

    private static PrimitiveBaseShape UnloadedMesh(UUID id)
    {
        PrimitiveBaseShape pbs = MeshPbs(Array.Empty<byte>());
        pbs.SculptTexture = id;
        return pbs;
    }

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    private static bool HasShape(PhysicsActor a)
    {
        object shape = typeof(JoltPrim).GetField("_shape", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a)!;
        return (bool)shape.GetType().GetProperty("IsValid")!.GetValue(shape)!;
    }

    private static int FailuresLogged(JoltScene scene) =>
        (int)(typeof(JoltScene).GetProperty("MeshAssetFailuresLogged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene)
              ?? typeof(JoltScene).GetField("MeshAssetFailuresLogged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene)
              ?? -1);

    private static PhysicsActor AddUnloaded(JoltScene scene, UUID id, Vector3 pos, Vector3 size, bool physical, bool phantom = false,
                                            byte shapeType = (byte)PhysShapeType.prim)
        => scene.AddPrimShape("unloaded", UnloadedMesh(id), pos, size, Quaternion.Identity, physical, phantom, shapeType, _id++);

    private static byte[] TroughAsset() => Asset(Soup(Trough), Trough, true);

    // ------------------------------------------------------------------ 1: the request, and the cook on arrival

    [Fact]
    public void A_mesh_prim_without_its_bytes_requests_the_asset_and_is_cooked_when_it_arrives()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor a = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + TroughHeight * 0.5f), Vector3.One, false);
            Assert.Equal(BoxKind, KindOf(a));
            Assert.Equal(1, assets.Requests.GetValueOrDefault(id));

            assets.Answer(id, TroughAsset());

            Assert.Equal("mesh(mesher)", KindOf(a));
            float rest = DropBall(scene, TroughHeight);
            _out.WriteLine($"MESH-ASSETS ball rests at {F(rest)} (floor 0.48, box top 1.70)");
            Assert.InRange(rest, 0.4f, 0.6f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_mesh_prim_whose_bytes_are_loaded_asks_for_nothing()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            PhysicsActor a = AddMesh(scene, TroughAsset(), TroughHeight, false, (byte)PhysShapeType.prim, out string kind);
            Assert.Equal("mesh(mesher)", kind);
            Assert.Equal(0, assets.Total);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: the re-cook keeps the body's state

    [Fact]
    public void The_recook_of_a_physical_body_keeps_its_place_turn_velocities_and_flags()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            Vector3 at = new(CentreX, CentreY, Ground + 30f);
            PhysicsActor a = AddUnloaded(scene, id, at, Vector3.One, true);
            a.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
            Step(scene, Heartbeat * 3);   // the body is woken and starts to fall
            a.Velocity = new Vector3(2f, 0f, 0f);
            a.RotationalVelocity = new Vector3(0f, 0f, 1.5f);
            Vector3 p0 = a.Position; Quaternion q0 = a.Orientation;

            assets.Answer(id, TroughAsset());   // no step between: only the swap is measured

            Assert.Equal("hull(mesher)", KindOf(a));
            Assert.True(Vector3.Distance(p0, a.Position) < 0.01f, $"moved {p0} -> {a.Position}");
            Assert.True(MathF.Abs(Quaternion.Dot(q0, a.Orientation)) > 0.9999f);
            Assert.True(a.IsPhysical);
            Step(scene, Heartbeat * 2);
            Assert.True(a.Velocity.X > 1.5f && a.Velocity.X < 2.5f, $"vx {a.Velocity.X}");
            Assert.True(MathF.Abs(a.RotationalVelocity.Z - 1.5f) < 0.3f, $"wz {a.RotationalVelocity.Z}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_recook_keeps_the_phantom_flag_and_a_phantom_body_still_passes_what_it_did_not_touch()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor phantom = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + 6f), Vector3.One, true, phantom: true);
            // a ball above the phantom, falling through it to the ground
            assets.Answer(id, TroughAsset());
            Assert.True(phantom.Phantom);
            Assert.Equal("hull(mesher)", KindOf(phantom));
            PhysicsActor ball = scene.AddPrimShape("ball", PrimitiveBaseShape.CreateSphere(), new Vector3(CentreX, CentreY, Ground + 12f),
                                                   new Vector3(0.4f, 0.4f, 0.4f), Quaternion.Identity, true, _id++);
            Step(scene, 4f);
            Assert.InRange(ball.Position.Z - Ground, 0f, 0.5f);   // reached the ground: the phantom let it through
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_recook_keeps_a_volume_detector_a_sensor()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor vd = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + TroughHeight * 0.5f), Vector3.One, false, phantom: true);
            vd.SetVolumeDetect(1);
            assets.Answer(id, TroughAsset());
            Assert.Equal("mesh(mesher)", KindOf(vd));
            // a sensor: a ball dropped on it falls through to the ground instead of resting on it
            float rest = DropBall(scene, TroughHeight);
            Assert.InRange(rest, 0f, 0.3f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_ball_resting_on_the_bounding_box_falls_to_the_real_floor_when_the_asset_arrives()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + TroughHeight * 0.5f), Vector3.One, false);
            PhysicsActor ball = scene.AddPrimShape("ball", PrimitiveBaseShape.CreateSphere(), new Vector3(CentreX, CentreY, Ground + TroughHeight + 0.6f),
                                                   new Vector3(0.4f, 0.4f, 0.4f), Quaternion.Identity, true, _id++);
            Step(scene, 5f);   // the ball settles on the box's top and sleeps
            float onBox = ball.Position.Z - Ground;
            Assert.True(onBox > 1.3f, $"ball on the box at {onBox}");

            assets.Answer(id, TroughAsset());
            Step(scene, 4f);   // its contacts are found again: it is woken and falls to the floor
            float onFloor = ball.Position.Z - Ground;
            _out.WriteLine($"MESH-ASSETS ball {F(onBox)} on the box, {F(onFloor)} after the asset");
            Assert.InRange(onFloor, 0.4f, 0.6f);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: a prim removed before its asset arrives

    [Fact]
    public void A_prim_removed_before_its_asset_arrives_is_not_touched()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor gone = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, false);
            PhysicsActor kept = AddUnloaded(scene, id, new Vector3(CentreX + 10f, CentreY, Ground + 5f), Vector3.One, false);
            Assert.Equal(1, assets.Requests[id]);
            scene.RemovePrim(gone);

            assets.Answer(id, TroughAsset());

            Assert.Equal(BoxKind, KindOf(gone));   // never re-cooked
            Assert.False(HasShape(gone));          // and no shape made for it
            Assert.Equal("mesh(mesher)", KindOf(kept));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 4: a missing or unreadable asset

    [Fact]
    public void A_missing_asset_keeps_the_box_and_is_logged_once()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor a = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, false);
            PhysicsActor b = AddUnloaded(scene, id, new Vector3(CentreX + 5f, CentreY, Ground + 5f), Vector3.One, false);
            assets.Answer(id, null);
            Assert.Equal(BoxKind, KindOf(a));
            Assert.Equal(BoxKind, KindOf(b));
            Assert.Equal(1, FailuresLogged(scene));

            // another prim of the same asset: still a box, no second request, no second log line
            PhysicsActor c = AddUnloaded(scene, id, new Vector3(CentreX + 9f, CentreY, Ground + 5f), Vector3.One, false);
            Assert.Equal(BoxKind, KindOf(c));
            Assert.Equal(1, assets.Requests[id]);
            Assert.Equal(1, FailuresLogged(scene));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_unreadable_asset_keeps_the_box_and_is_logged_once()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor a = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, false);
            PhysicsActor b = AddUnloaded(scene, id, new Vector3(CentreX + 5f, CentreY, Ground + 5f), Vector3.One, true);
            assets.Answer(id, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Equal(BoxKind, KindOf(a));
            Assert.Equal(BoxKind, KindOf(b));
            Assert.Equal(1, FailuresLogged(scene));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_failed_asset_is_asked_for_again_after_the_retry_wait()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            long now = 1_000_000;
            typeof(JoltScene).GetField("MeshAssetClockMs", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(scene, (Func<long>)(() => now));
            UUID id = UUID.Random();
            AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + 5f), Vector3.One, false);
            assets.Answer(id, null);
            now += 60_000;
            AddUnloaded(scene, id, new Vector3(CentreX + 4f, CentreY, Ground + 5f), Vector3.One, false);
            Assert.Equal(1, assets.Requests[id]);   // inside the wait
            now += 11 * 60_000;
            PhysicsActor late = AddUnloaded(scene, id, new Vector3(CentreX + 8f, CentreY, Ground + 5f), Vector3.One, false);
            Assert.Equal(2, assets.Requests[id]);   // after it: a second try
            assets.Answer(id, TroughAsset());
            Assert.Equal("mesh(mesher)", KindOf(late));
            Assert.Equal(1, FailuresLogged(scene));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 5: many prims of the same mesh, one fetch

    [Fact]
    public void Many_prims_of_the_same_mesh_cause_one_request()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            var prims = new List<PhysicsActor>();
            for (int i = 0; i < 25; i++)
                prims.Add(AddUnloaded(scene, id, new Vector3(CentreX + i * 3f, CentreY, Ground + 5f), i % 2 == 0 ? Vector3.One : new Vector3(1.5f, 1f, 1f), false));
            Assert.Equal(1, assets.Requests[id]);
            assets.Answer(id, TroughAsset());
            Assert.All(prims, p => Assert.Equal("mesh(mesher)", KindOf(p)));
            Assert.Equal(1, assets.Total);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 6: a linkset whose child is a mesh

    // A triangular prism, 6 points: its hull is half its bounding box, so its mass shows whether the child's shape arrived.
    private static (Vector3[] verts, int[] tris) Wedge()
    {
        Vector3[] v =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f),
            new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, 0.5f),
        };
        int[] t = { 0, 1, 2, 3, 5, 4, 0, 3, 4, 0, 4, 1, 0, 2, 5, 0, 5, 3, 1, 4, 5, 1, 5, 2 };
        return (v, t);
    }

    [Fact]
    public void A_linkset_child_that_is_a_mesh_gets_its_shape_when_the_asset_arrives()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            Vector3 at = new(CentreX, CentreY, Ground + 10f);
            PhysicsActor root = scene.AddPrimShape("root", PrimitiveBaseShape.CreateBox(), at, Vector3.One, Quaternion.Identity, true, _id++);
            PhysicsActor child = AddUnloaded(scene, id, at + new Vector3(1f, 0f, 0f), Vector3.One, true);
            child.link(root);
            Step(scene, Heartbeat * 2);
            float rootBodyBefore = ((JoltPrim)root).BodyMass;

            assets.Answer(id, Asset(Wedge(), new[] { new Box(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f) }, true));
            Step(scene, Heartbeat * 2);   // the root rebuilds its compound at the top of a step

            Assert.Equal("hull(mesher)", KindOf(child));
            float rootBodyAfter = ((JoltPrim)root).BodyMass;
            _out.WriteLine($"MESH-ASSETS linkset body mass {F(rootBodyBefore)} -> {F(rootBodyAfter)}");
            Assert.True(rootBodyAfter < rootBodyBefore * 0.85f, $"{rootBodyBefore} -> {rootBodyAfter}");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 7: the small-mesh rule

    [Theory]
    [InlineData(0.04f, false, "box(small mesh)")]    // non-physical, under 0.05 on every side: a box
    [InlineData(0.06f, false, "mesh(mesher)")]       // over 0.05: the real mesh (the mesher's own rule was 0.2)
    [InlineData(0.15f, false, "mesh(mesher)")]
    [InlineData(0.04f, true, "hull(mesher)")]        // physical: never a box on size
    [InlineData(0.15f, true, "hull(mesher)")]
    public void Only_a_non_physical_mesh_under_five_centimetres_on_every_side_is_a_box(float side, bool physical, string expected)
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            PhysicsActor a = AddMesh(scene, TroughAsset(), 1f, physical, (byte)PhysShapeType.prim, out string kind, new Vector3(side, side, side));
            Assert.Equal(expected, kind);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_non_physical_mesh_with_one_side_over_five_centimetres_is_not_a_box()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            AddMesh(scene, TroughAsset(), 1f, false, (byte)PhysShapeType.prim, out string kind, new Vector3(0.04f, 0.04f, 0.5f));
            Assert.Equal("mesh(mesher)", kind);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_tiny_non_physical_mesh_asks_for_no_asset()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            PhysicsActor a = AddUnloaded(scene, UUID.Random(), new Vector3(CentreX, CentreY, Ground + 5f), new Vector3(0.03f, 0.03f, 0.03f), false);
            Assert.Equal("box(small mesh)", KindOf(a));
            Assert.Equal(0, assets.Total);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 8: CONVEX on a non-physical mesh, behind a setting

    [Theory]
    [InlineData(false, (byte)PhysShapeType.convex, "mesh(mesher)")]   // off (default): as before, whatever the type
    [InlineData(false, (byte)PhysShapeType.prim, "mesh(mesher)")]
    [InlineData(true, (byte)PhysShapeType.convex, "hull(mesher)")]    // on: CONVEX is one hull
    [InlineData(true, (byte)PhysShapeType.prim, "mesh(mesher)")]      // on: PRIM stays the triangle mesh
    public void A_non_physical_mesh_marked_convex_is_one_hull_only_when_the_setting_is_on(bool on, byte type, string expected)
    {
        var assets = new FakeAssets();
        JoltScene scene = on ? SceneWith(assets, Scratch(), ("HonourConvexMesh", "true")) : SceneWith(assets, Scratch());
        try
        {
            PhysicsActor a = AddMesh(scene, TroughAsset(), TroughHeight, false, type, out string kind);
            Assert.Equal(expected, kind);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Switching_a_non_physical_mesh_to_convex_recooks_it_only_when_the_setting_is_on()
    {
        foreach (bool on in new[] { false, true })
        {
            var assets = new FakeAssets();
            JoltScene scene = on ? SceneWith(assets, Scratch(), ("HonourConvexMesh", "true")) : SceneWith(assets, Scratch());
            try
            {
                PhysicsActor a = AddMesh(scene, TroughAsset(), TroughHeight, false, (byte)PhysShapeType.prim, out _);
                Assert.Equal("mesh(mesher)", KindOf(a));
                a.PhysicsShapeType = (byte)PhysShapeType.convex;
                // the asset bytes were dropped by the mesher after the first decode; the cached mesh serves the same size
                Assert.Equal(on ? "hull(mesher)" : "mesh(mesher)", KindOf(a));
            }
            finally { scene.Dispose(); }
        }
    }

    // ------------------------------------------------------------------ the gateway and the trough, fetched

    // The recon's gateway (opening 1.6 m wide, 2.1 m tall) and open trough (slot 3.4 m, floor top 0.3 m), with the asset
    // fetched through the delegate instead of being in the shape. Non-physical: the ball rests on the floor, and a box and an
    // avatar pass the opening. Physical: one hull of every point, as before the fetch existed.
    private static PhysicsActor AddFetched(JoltScene scene, FakeAssets assets, byte[] asset, float height, bool physical, byte type, out string kind)
    {
        UUID id = UUID.Random();
        PhysicsActor a = AddUnloaded(scene, id, new Vector3(CentreX, CentreY, Ground + height * 0.5f), Vector3.One, physical, false, type);
        Assert.Equal(1, assets.Requests[id]);
        assets.Answer(id, asset);
        kind = KindOf(a);
        return a;
    }

    [Fact]
    public void The_trough_and_the_gateway_through_the_fetch_path()
    {
        var lines = new List<string>();
        (Vector3[] verts, int[] tris) trough = Soup(Trough), gate = Soup(Gateway);
        foreach (bool physical in new[] { false, true })
            foreach (byte type in new byte[] { (byte)PhysShapeType.prim, (byte)PhysShapeType.convex })
            {
                string shape = type == (byte)PhysShapeType.prim ? "PRIM" : "CONVEX";
                float rest, boxPast, avPast;
                string kind, kind2;

                var a1 = new FakeAssets();
                JoltScene s1 = SceneWith(a1, Scratch());
                try
                {
                    AddFetched(s1, a1, Asset(trough, Trough, true), TroughHeight, physical, type, out kind);
                    Step(s1, 2f);
                    rest = DropBall(s1, TroughHeight);
                }
                finally { s1.Dispose(); }

                var a2 = new FakeAssets();
                JoltScene s2 = SceneWith(a2, Scratch());
                try
                {
                    AddFetched(s2, a2, Asset(gate, Gateway, true), GatewayHeight, physical, type, out kind2);
                    Step(s2, 2f);
                    boxPast = FireBox(s2);
                }
                finally { s2.Dispose(); }

                var a3 = new FakeAssets();
                JoltScene s3 = SceneWith(a3, Scratch());
                try
                {
                    AddFetched(s3, a3, Asset(gate, Gateway, true), GatewayHeight, physical, type, out _);
                    Step(s3, 2f);
                    avPast = WalkAvatar(s3);
                }
                finally { s3.Dispose(); }

                lines.Add($"MESH-ASSETS fetched physical={physical} shape={shape} cooked_trough={kind} cooked_gateway={kind2} " +
                          $"ball_rest_z={F(rest)} box_past_far_side={F(boxPast)} avatar_past_far_side={F(avPast)}");
                if (physical)
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
        foreach (string l in lines) _out.WriteLine(l);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }

    // ------------------------------------------------------------------ sculpted prims: the same fetch

    // A sculpt map: a 64 x 64 grid whose red and green are the point's x and y and whose blue is a gentle bump, which the
    // mesher reads as a sheet (plane sculpt) of that height.
    private static SkiaSharp.SKBitmap SculptMap()
    {
        var bmp = new SkiaSharp.SKBitmap(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                byte z = (byte)(96 + 64 * Math.Sin(Math.PI * x / 63.0) * Math.Sin(Math.PI * y / 63.0));
                bmp.SetPixel(x, y, new SkiaSharp.SKColor((byte)(x * 255 / 63), (byte)(y * 255 / 63), z));
            }
        return bmp;
    }

    private static byte[] SculptMapJ2k()
    {
        using var bmp = SculptMap();
        return J2kImage.ToBytes(bmp);
    }

    private static PrimitiveBaseShape UnloadedSculpt(UUID id, SculptType type = SculptType.Plane)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;
        pbs.ProfileCurve = 0;
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)type;
        pbs.SculptTexture = id;
        pbs.SculptData = Array.Empty<byte>();
        return pbs;
    }

    private static PhysicsActor AddSculpt(JoltScene scene, PrimitiveBaseShape pbs, bool physical)
        => scene.AddPrimShape("sculpt", pbs, new Vector3(CentreX, CentreY, Ground + 2f), new Vector3(2f, 2f, 2f), Quaternion.Identity, physical,
                              false, (byte)PhysShapeType.prim, _id++);

    [Theory]
    [InlineData(SculptType.Plane)]
    [InlineData(SculptType.Sphere)]
    [InlineData(SculptType.Torus)]
    [InlineData(SculptType.Cylinder)]
    public void A_sculpted_prim_without_its_map_requests_the_asset_and_is_cooked_when_it_arrives(SculptType type)
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor a = AddSculpt(scene, UnloadedSculpt(id, type), false);
            Assert.Equal(BoxKind, KindOf(a));
            Assert.Equal(1, assets.Requests.GetValueOrDefault(id));

            assets.Answer(id, SculptMapJ2k());

            Assert.Equal("mesh(mesher)", KindOf(a));
            Assert.Equal(1, assets.Total);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_physical_sculpted_prim_is_cooked_as_a_hull_when_its_map_arrives()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID id = UUID.Random();
            PhysicsActor a = AddSculpt(scene, UnloadedSculpt(id, SculptType.Sphere), true);
            Assert.Equal(BoxKind, KindOf(a));
            assets.Answer(id, SculptMapJ2k());
            Assert.Equal("hull(mesher)", KindOf(a));
            Assert.True(a.IsPhysical);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_sculpted_prim_whose_map_is_already_decoded_on_disk_asks_for_nothing()
    {
        var assets = new FakeAssets();
        string scratch = Scratch();
        JoltScene scene = SceneWith(assets, scratch);
        try
        {
            UUID id = UUID.Random();
            using (var bmp = SculptMap())
            using (var image = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(Path.Combine(scratch, "smap_" + id)))
                png.SaveTo(fs);
            PhysicsActor a = AddSculpt(scene, UnloadedSculpt(id), false);
            Assert.Equal("mesh(mesher)", KindOf(a));
            Assert.Equal(0, assets.Total);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_sculpted_prim_whose_map_is_missing_or_unreadable_stays_a_box_and_is_logged_once()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            UUID missing = UUID.Random(), junk = UUID.Random();
            PhysicsActor a = AddSculpt(scene, UnloadedSculpt(missing), false);
            PhysicsActor b = AddSculpt(scene, UnloadedSculpt(junk), false);
            assets.Answer(missing, null);
            assets.Answer(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Equal(BoxKind, KindOf(a));
            Assert.Equal(BoxKind, KindOf(b));
            Assert.Equal(2, FailuresLogged(scene));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_plain_prim_asks_for_no_asset()
    {
        var assets = new FakeAssets();
        JoltScene scene = SceneWith(assets, Scratch());
        try
        {
            scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY, Ground + 2f), Vector3.One,
                               Quaternion.Identity, false, _id++);
            Assert.Equal(0, assets.Total);
        }
        finally { scene.Dispose(); }
    }
}
