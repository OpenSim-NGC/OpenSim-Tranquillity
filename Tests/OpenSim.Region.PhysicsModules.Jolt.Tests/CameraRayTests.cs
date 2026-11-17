/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The viewer camera's collision ray. Core asks the physics scene whether it answers rays (SupportsRayCast) and, when the
/// camera moves, casts from the avatar's head toward the camera (ScenePresence.checkCameraCollision, through the
/// RaycastWorld overload with a RaycastCallback); the closest hit becomes the camera constraint plane sent to the viewer.
/// ubODE answers it (ODEScene.SupportsRayCast, RaycastWorld): the request is queued and cast on the heartbeat against
/// prims of every kind but not the terrain (RayFilterFlags.AllPrims, closest hit), at most 30 ms of rays a heartbeat
/// (ODERayCastRequestManager.ProcessQueuedRequests), and every request gets its callback, a miss included, since core waits
/// for it before casting again. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class CameraRayTests
{
    private static uint _id = 48300;

    private sealed class Answers
    {
        public readonly List<(bool hit, Vector3 point, uint id, float distance, Vector3 normal)> Got = new();
        public void Callback(bool hit, Vector3 point, uint id, float distance, Vector3 normal) => Got.Add((hit, point, id, distance, normal));
    }

    // A 4 m x 0.5 m x 4 m wall (non-physical box) whose face toward -X is at x = CentreX + 2.75.
    private static JoltPrim Wall(JoltScene scene, bool phantom = false)
    {
        var p = (JoltPrim)scene.AddPrimShape("wall", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX + 3f, CentreY, 30f),
                                             new Vector3(0.5f, 4f, 4f), Quaternion.Identity, false, _id++);
        if (phantom) p.Phantom = true;
        return p;
    }

    [Fact]
    public void The_scene_answers_the_camera_ray_by_default()
    {
        JoltScene scene = NewScene(Scratch());
        try { Assert.True(scene.SupportsRayCast()); }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_camera_budget_of_zero_leaves_the_camera_ray_unanswered_as_before()
    {
        JoltScene scene = NewScene(Scratch(), ("CameraRayBudgetMs", "0"));
        try { Assert.False(scene.SupportsRayCast()); }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_camera_ray_toward_a_wall_hits_its_face_on_the_heartbeat()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim wall = Wall(scene);
            var a = new Answers();
            scene.RaycastWorld(new Vector3(CentreX, CentreY, 30f), Vector3.UnitX, 6f, a.Callback);
            Step(scene, 1f / 11f);
            Assert.Single(a.Got);
            var r = a.Got[0];
            Assert.True(r.hit, "the ray toward the wall missed");
            Assert.Equal(wall.LocalID, r.id);
            Assert.InRange(r.distance, 2.70f, 2.80f);
            Assert.InRange(r.point.X, CentreX + 2.70f, CentreX + 2.80f);
            Assert.True(r.normal.X < -0.99f, $"the normal {r.normal} does not face back along the ray");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_phantom_wall_is_hit_as_ubODE_hits_every_kind_of_prim()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim wall = Wall(scene, phantom: true);
            var a = new Answers();
            scene.RaycastWorld(new Vector3(CentreX, CentreY, 30f), Vector3.UnitX, 6f, a.Callback);
            Step(scene, 1f / 11f);
            Assert.Single(a.Got);
            Assert.True(a.Got[0].hit, "the ray toward the phantom wall missed");
            Assert.Equal(wall.LocalID, a.Got[0].id);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_camera_ray_with_nothing_in_the_way_is_answered_with_a_miss_and_the_terrain_is_not_hit()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Wall(scene);
            var a = new Answers();
            scene.RaycastWorld(new Vector3(CentreX, CentreY, 30f), -Vector3.UnitX, 6f, a.Callback);   // away from the wall
            scene.RaycastWorld(new Vector3(CentreX - 20f, CentreY, 30f), -Vector3.UnitZ, 60f, a.Callback);   // down to the ground
            Step(scene, 1f / 11f);
            Assert.Equal(2, a.Got.Count);
            Assert.False(a.Got[0].hit);
            Assert.False(a.Got[1].hit, "the camera ray hit the terrain; ubODE's camera ray tests prims only");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Rays_past_the_heartbeat_budget_wait_for_the_next_heartbeat_and_all_are_answered()
    {
        JoltScene scene = NewScene(Scratch(), ("CameraRayBudgetMs", "0.1"));
        try
        {
            Wall(scene);
            var a = new Answers();
            const int Total = 5000;
            for (int i = 0; i < Total; i++)
                scene.RaycastWorld(new Vector3(CentreX, CentreY, 30f), Vector3.UnitX, 6f, a.Callback);
            Step(scene, 1f / 11f);
            int afterOne = a.Got.Count;
            Assert.InRange(afterOne, 1, Total - 1);
            for (int i = 0; i < 2000 && a.Got.Count < Total; i++)
                Step(scene, 1f / 11f);
            Assert.Equal(Total, a.Got.Count);
            Assert.All(a.Got, r => Assert.True(r.hit));
        }
        finally { scene.Dispose(); }
    }
}
