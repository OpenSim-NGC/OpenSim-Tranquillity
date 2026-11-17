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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Sitting on a part with no sit target (PhysicsScene.SitAvatar, called by ScenePresence.PhysicsSit). The expected seats
/// are worked out by hand from ubODE's rules (OpenSim.Region.PhysicsModules.ubODE/ODESitAvatar.cs) for each case, with
/// core's avatar offset (0.35, 0, 0.65) (ScenePresence.PhysicsSit); the working is in each test's comment. Status: below 0
/// ignore, 0 no suitable surface, 1 seated, 3 seated on a wide top. Offsets and orientations are relative to the part.
/// Serial with the other native tests (the native library is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicsSitTests
{
    private readonly ITestOutputHelper _out;
    public PhysicsSitTests(ITestOutputHelper output) { _out = output; }

    private const float Ground = 25f;
    private const float Tol = 0.01f;
    private static readonly Vector3 AvOffset = new(0.35f, 0f, 0.65f);   // ScenePresence.PhysicsSit
    private static readonly Vector3 C = new(128f, 128f, 25.25f);        // a 0.5 m box resting on the ground
    private static uint _id = 52000;

    // The shared scene with the prim mesher wired in (a hollow box is cooked from its triangles).
    private static JoltScene NewScene(bool? physicsSit = null)
        => physicsSit.HasValue
            ? MeshShapesTodayTests.NewScene(MeshShapesTodayTests.Scratch(), ("PhysicsSit", physicsSit.Value ? "true" : "false"))
            : MeshShapesTodayTests.NewScene(MeshShapesTodayTests.Scratch());

    private static PhysicsActor Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 at, Vector3 size, Quaternion rot, bool phantom = false)
    {
        PhysicsActor a = scene.AddPrimShape("sit", pbs, at, size, rot, false, phantom, (byte)PhysShapeType.prim, _id++);
        scene.Simulate(1f / 11f);
        return a;
    }

    private record SitAnswer(int Returned, bool Answered, int Status, uint Part, Vector3 Offset, Quaternion Orientation);

    // SitAvatar answers through the callback, on another thread (as ubODE): wait for it, with a generous cap.
    private SitAnswer Sit(JoltScene scene, PhysicsActor part, Vector3 camera, Vector3 clickOffset)
    {
        using var done = new ManualResetEventSlim(false);
        int status = int.MinValue; uint id = 0; Vector3 off = Vector3.Zero; Quaternion ori = Quaternion.Identity;
        int returned = scene.SitAvatar(part, camera, camera, clickOffset, AvOffset, (s, p, o, q) =>
        {
            status = s; id = p; off = o; ori = q;
            done.Set();
        });
        bool answered = returned != 0 && done.Wait(TimeSpan.FromSeconds(10));
        var a = new SitAnswer(returned, answered, status, id, off, ori);
        _out.WriteLine($"SIT returned={returned} answered={answered} status={status} part={id} offset={off} ori={ori}");
        return a;
    }

    private static void Seated(SitAnswer a, PhysicsActor part, int status, Vector3 offset, Quaternion ori)
    {
        Assert.True(a.Returned != 0, "SitAvatar returned 0: the engine left the sit to core's fallback");
        Assert.True(a.Answered, "no answer came through the callback");
        Assert.Equal(status, a.Status);
        Assert.Equal(part.LocalID, a.Part);
        Assert.True(Vector3.Distance(offset, a.Offset) < Tol, $"offset {a.Offset}, expected {offset}");
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Orientation), ori));
        Assert.True(dot > 0.9999f, $"orientation {a.Orientation}, expected {ori} (|dot| {dot})");
    }

    private static Quaternion AboutZ(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
    private static Quaternion AboutY(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);

    // Camera at (125, 128, 27.25), above and behind on -X; click the top centre (offset (0, 0, 0.25)).
    // The ray hits the top at (128, 128, 25.5), normal up (> 0.866): the edge search. From 1 cm under the top, rays
    // toward the camera (-X), then -Y, +Y, +X each leave the box after 0.25 m; the first (-X) is kept (later ones are
    // not shorter), none under 0.2 m. Edge at (127.75, 128, 25.49), normal (-1, 0); avatar offset X halved (0.175).
    // RotAroundZ(-1, 0) = 180 degrees about Z, so the offset turns to (-0.175, 0, 0.65):
    // seat = (127.75 - 0.175, 128, 25.49 + 0.65) - C = (-0.425, 0, 0.89), status 1.
    [Fact]
    public void A_box_clicked_on_top_from_above_and_behind_seats_at_the_edge_toward_the_camera()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f));
            Seated(a, box, 1, new Vector3(-0.425f, 0f, 0.89f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // Camera at (124, 128, 25.25) level with the -X face; click its centre (offset (-0.25, 0, 0)). Normal (-1, 0, 0),
    // z under 0.5: the side rule. 1 cm in (x 127.76), a ray up (-n.x n.z, -n.y n.z, 1 - n.z^2) = (0, 0, 1) leaves the top
    // at z 25.5 with normal z 1 (>= 0.7): edge at (127.76, 128, 25.5); the edge normal stays the face's (-1, 0), the edge
    // direction minus the first ray's (-1, 0); avatar X halved. 180 degrees about Z:
    // seat = (127.76 - 0.175, 128, 25.5 + 0.65) - C = (-0.415, 0, 0.9), status 1.
    [Fact]
    public void A_box_clicked_on_a_side_face_seats_on_the_top_edge_above_it()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            SitAnswer a = Sit(scene, box, new Vector3(124f, 128f, 25.25f), new Vector3(-0.25f, 0f, 0f));
            Seated(a, box, 1, new Vector3(-0.415f, 0f, 0.9f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // A 1 m sphere (r 0.5) at (128, 128, 25.5). Camera (125, 128, 26); click (-0.4, 0, 0.3), a point on the sphere facing
    // the camera. Sphere rule: offset = hit - centre, scaled to r: (-0.4, 0, 0.3). RotAroundZ(-0.4, 0) = 180 degrees.
    // r >= 0.4 and z 0.3 < 0.4: t = 1 / (r^2 - z^2) = 6.25, x = -0.4 * 6.25 = -2.5, z = 0.4, then x *= r^2 - 0.16 = 0.09:
    // x = -0.225. Plus the avatar offset turned 180 degrees (-0.35, 0, 0.65): (-0.575, 0, 1.05), status 1.
    [Fact]
    public void A_sphere_seats_by_the_sphere_rule()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor ball = Add(scene, PrimitiveBaseShape.CreateSphere(), new Vector3(128f, 128f, 25.5f), Vector3.One, Quaternion.Identity);
            Assert.Equal("sphere", ((JoltPrim)ball).ShapeKind);
            SitAnswer a = Sit(scene, ball, new Vector3(125f, 128f, 26f), new Vector3(-0.4f, 0f, 0.3f));
            Seated(a, ball, 1, new Vector3(-0.575f, 0f, 1.05f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // The 0.5 m box turned 15 degrees about Z. Camera (125, 128, 27.25); click the top 0.1 m toward the camera (world
    // offset (-0.1, 0, 0.25)). Edge search: the -X ray from (127.9, 128, 25.49) meets the face with normal
    // (-cos15, -sin15) after 0.25 / cos15 - 0.1 = 0.1588 m (< 0.2: the search stops). Edge (127.7412, 128, 25.49).
    // RotAroundZ(-cos15, -sin15) = -165 degrees; avatar (0.175, 0, 0.65) turned: (-0.16904, -0.04529, 0.65).
    // World seat - C = (-0.42784, -0.04529, 0.89); in the box's frame (turned -15 degrees): (-0.42498, 0.06698, 0.89),
    // orientation -15 + -165 = 180 degrees about Z, status 1.
    [Fact]
    public void A_rotated_box_seats_in_its_own_frame()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), AboutZ(15f));
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(-0.1f, 0f, 0.25f));
            Seated(a, box, 1, new Vector3(-0.42498f, 0.06698f, 0.89f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // A 2 x 2 x 0.5 box at (128, 128, 26) tipped 40 degrees about Y: top normal (sin40, 0, cos40), z 0.766, between 0.5
    // and 0.866, so neither search runs. Camera (131, 128, 29); click the top centre, world offset 0.25 n =
    // (0.1607, 0, 0.1915). Edge = the hit, normal (0.6428, 0); the edge direction is minus the camera ray, +X, so no flip;
    // RotAroundZ(0.6428, 0) = identity; avatar offset not halved. World seat (0.5107, 0, 0.8415), turned -40 degrees about
    // Y: (0.5107 cos40 - 0.8415 sin40, 0, 0.5107 sin40 + 0.8415 cos40) = (-0.1497, 0, 0.9729); orientation -40 degrees
    // about Y, status 1.
    [Fact]
    public void A_sloped_prim_seats_on_the_slope_where_clicked()
    {
        JoltScene scene = NewScene();
        try
        {
            Quaternion tip = AboutY(40f);
            PhysicsActor slab = Add(scene, PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, 26f), new Vector3(2f, 2f, 0.5f), tip);
            Vector3 top = new Vector3(0f, 0f, 0.25f) * tip;
            SitAnswer a = Sit(scene, slab, new Vector3(131f, 128f, 29f), top);
            Seated(a, slab, 1, new Vector3(-0.1497f, 0f, 0.9729f), AboutY(-40f));
        }
        finally { scene.Dispose(); }
    }

    // A 2 x 2 x 0.5 box hollowed 50% (cooked from the mesher's triangles, not a native box) at (128, 128, 25.25). Camera
    // (125, 128, 28.5); click the ring's top at (-0.85, 0, 0.25). Edge search: the -X ray from 1 cm under the top leaves by
    // the outer wall (x 127) after 0.15 m (a back face; < 0.2, the search stops). Edge (127, 128, 25.49), normal (-1, 0)
    // (either sign: it is turned to face along the ray). Avatar X halved, 180 degrees:
    // seat = (127 - 0.175, 128, 25.49 + 0.65) - C = (-1.175, 0, 0.89), status 1.
    [Fact]
    public void A_mesh_seats_by_the_same_edge_rule()
    {
        JoltScene scene = NewScene();
        try
        {
            PrimitiveBaseShape hollow = PrimitiveBaseShape.CreateBox();
            hollow.ProfileHollow = 25000;   // 50%
            PhysicsActor ring = Add(scene, hollow, C, new Vector3(2f, 2f, 0.5f), Quaternion.Identity);
            Assert.Equal("mesh(mesher)", ((JoltPrim)ring).ShapeKind);
            SitAnswer a = Sit(scene, ring, new Vector3(125f, 128f, 28.5f), new Vector3(-0.85f, 0f, 0.25f));
            Seated(a, ring, 1, new Vector3(-1.175f, 0f, 0.89f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // ubODE's actor ray takes phantom parts (RayFilterFlags.AllPrims), so a phantom part with an actor (core gives one to a
    // volume detector) seats as a solid one: the numbers of the first test.
    [Fact]
    public void A_phantom_part_seats_as_a_solid_one()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, phantom: true);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f));
            Seated(a, box, 1, new Vector3(-0.425f, 0f, 0.89f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // Another part in front of the clicked one does not stop the ray (ubODE casts at the one part only): a wall between the
    // camera and the box changes nothing, the numbers of the first test.
    [Fact]
    public void A_part_behind_another_is_still_sat_on()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            Add(scene, PrimitiveBaseShape.CreateBox(), new Vector3(126.5f, 128f, 26.5f), new Vector3(0.2f, 4f, 3f), Quaternion.Identity);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f));
            Seated(a, box, 1, new Vector3(-0.425f, 0f, 0.89f), AboutZ(180f));
        }
        finally { scene.Dispose(); }
    }

    // A clicked point the ray to the part never reaches (5 m over the box, seen from the side): no hit, status 0, so core
    // sends "There is no suitable surface to sit on".
    [Fact]
    public void A_point_the_ray_misses_is_no_suitable_surface()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 5f));
            Assert.True(a.Returned != 0 && a.Answered, "no answer came through the callback");
            Assert.Equal(0, a.Status);
            Assert.Equal(box.LocalID, a.Part);
        }
        finally { scene.Dispose(); }
    }

    // ubODE ignores (status -1) an actor the scene no longer has. Core never asks the engine about an attachment
    // (ScenePresence.PhysicsSit returns first), and an attachment's parts have no actor; a removed part is the engine's case.
    [Fact]
    public void A_removed_part_is_ignored()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            scene.RemovePrim(box);
            scene.Simulate(1f / 11f);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f));
            Assert.True(a.Returned != 0 && a.Answered, "no answer came through the callback");
            Assert.Equal(-1, a.Status);
        }
        finally { scene.Dispose(); }
    }

    // [Jolt] PhysicsSit = false: the engine answers nothing (SitAvatar returns 0, no callback), so core seats the avatar at
    // the clicked point raised by half its height, as before.
    [Fact]
    public void With_PhysicsSit_off_the_engine_leaves_the_sit_to_core()
    {
        JoltScene scene = NewScene(physicsSit: false);
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            bool called = false;
            int returned = scene.SitAvatar(box, Vector3.Zero, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f), AvOffset,
                (s, p, o, q) => called = true);
            Thread.Sleep(500);   // a "did not happen" window: nothing may answer
            Assert.Equal(0, returned);
            Assert.False(called);
        }
        finally { scene.Dispose(); }
    }

    // On by default: a [Jolt] section without the key sits through the engine.
    [Fact]
    public void PhysicsSit_is_on_by_default()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            SitAnswer a = Sit(scene, box, new Vector3(125f, 128f, 27.25f), new Vector3(0f, 0f, 0.25f));
            Assert.NotEqual(0, a.Returned);
            Assert.True(a.Answered);
        }
        finally { scene.Dispose(); }
    }

    // RaycastActor hits only the part it is given: the wall in front is passed through, and the part's top is hit.
    [Fact]
    public void RaycastActor_hits_only_the_part_it_is_given()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor box = Add(scene, PrimitiveBaseShape.CreateBox(), C, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity);
            Add(scene, PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, 27f), new Vector3(4f, 4f, 0.2f), Quaternion.Identity);
            List<ContactResult> hits = scene.RaycastActor(box, new Vector3(128f, 128f, 30f), -Vector3.UnitZ, 10f, 1, RayFilterFlags.AllPrims | RayFilterFlags.ClosestHit);
            _out.WriteLine($"RAYCASTACTOR hits={hits.Count} {(hits.Count > 0 ? hits[0].Pos.ToString() : "")}");
            Assert.Single(hits);
            Assert.Equal(box.LocalID, hits[0].ConsumerID);
            Assert.True(MathF.Abs(hits[0].Pos.Z - 25.5f) < Tol, $"hit at {hits[0].Pos}");
            Assert.True(hits[0].Normal.Z > 0.99f, $"normal {hits[0].Normal}");
        }
        finally { scene.Dispose(); }
    }
}
