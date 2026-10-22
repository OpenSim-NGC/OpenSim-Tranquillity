/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Quaternion = OpenMetaverse.Quaternion;
using Vector3 = OpenMetaverse.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a rotation target (llRotLookAt, llLookAt, the rotate-drag) gives back when its body or its region goes away: the
/// scene's list of prims it steps each frame, the body and its shape, and a step or a setter that arrives afterwards.
/// Serial: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RotationTargetTeardownTests
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static int Targeted(JoltScene scene)
    {
        var set = (HashSet<JoltPrim>)typeof(JoltScene).GetField("_targeted", Private)!.GetValue(scene)!;
        lock (set) return set.Count;
    }

    private static int RegisteredShapes(JoltPhysicsBackend b)
        => ((HandleTable<JoltShapeRecord>)typeof(JoltPhysicsBackend).GetField("_shapes", Private)!.GetValue(b)!).Count;

    private static void TurnOn(PhysicsActor a)
    {
        a.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.5f);
        a.APIDStrength = 1f;
        a.APIDDamping = 0.5f;
        a.APIDActive = true;
    }

    /// <summary>Runs a cube in the air with its rotation target on; <paramref name="at"/> is called once, at the given
    /// heartbeat, and the run goes on for a second after it.</summary>
    private static void Run(int atHeartbeat, Action<Run, PhysicsActor> at, Action<Run, PhysicsActor> afterwards)
    {
        PhysicsActor actor = null;
        var sc = new Scenario
        {
            Name = "rotation-target-teardown",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                actor = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, 150f), Quaternion.Identity);
                TurnOn(actor);
            },
            Input = r =>
            {
                if (r.Heartbeats == atHeartbeat) at(r, actor);
                else if (r.Heartbeats > atHeartbeat) afterwards?.Invoke(r, actor);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
    }

    [Fact]
    public void A_body_with_a_rotation_target_that_is_removed_leaves_the_steps_list_and_frees_its_body_and_shape()
    {
        int targetedBefore = -1, targetedAfter = -1, shapesBefore = -1, shapesAfter = -1, bodiesBefore = -1, bodiesAfter = -1;
        int framesAfter = 0;
        Run(8,
            (r, a) =>
            {
                var scene = (JoltScene)r.PhysicsScene;
                targetedBefore = Targeted(scene);
                shapesBefore = r.Capacity.LiveShapeCount;
                bodiesBefore = r.Capacity.LiveBodyCount;
                scene.RemovePrim(a);
                targetedAfter = Targeted(scene);
                shapesAfter = r.Capacity.LiveShapeCount;
                bodiesAfter = r.Capacity.LiveBodyCount;
            },
            (r, a) =>
            {
                framesAfter++;
                // A late write from a script that has not heard the object is gone must not throw.
                a.APIDTarget = Quaternion.Identity;
                a.APIDActive = true;
            });
        Assert.Equal(1, targetedBefore);
        Assert.Equal(0, targetedAfter);
        Assert.Equal(bodiesBefore - 1, bodiesAfter);
        Assert.True(shapesAfter < shapesBefore, $"the shape stayed registered ({shapesBefore} -> {shapesAfter})");
        Assert.True(framesAfter > 0);
    }

    [Fact]
    public void A_rotation_target_switched_on_for_a_removed_prim_does_not_put_it_back_on_the_steps_list()
    {
        int framesAfter = 0, onTheList = -1;
        Run(8,
            (r, a) =>
            {
                var scene = (JoltScene)r.PhysicsScene;
                scene.RemovePrim(a);
                // A script that has not heard the object is gone turns the target on again.
                a.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
                a.APIDActive = true;
            },
            (r, a) =>
            {
                framesAfter++;
                onTheList = Targeted((JoltScene)r.PhysicsScene);
            });
        Assert.True(framesAfter > 0);
        Assert.Equal(0, onTheList);
    }

    [Fact]
    public void A_region_removed_while_a_rotation_target_is_on_frees_its_native_shapes_and_a_late_step_or_write_is_harmless()
    {
        JoltPhysicsBackend backend = null;
        int shapesBefore = -1, shapesAfter = -1;
        bool backendGone = false;
        int framesAfter = 0;
        Run(8,
            (r, a) =>
            {
                var scene = (JoltScene)r.PhysicsScene;
                backend = (JoltPhysicsBackend)scene.Backend;
                shapesBefore = RegisteredShapes(backend);
                scene.RemoveRegion(null);
                backendGone = scene.Backend == null;
                shapesAfter = RegisteredShapes(backend);
            },
            (r, a) =>
            {
                framesAfter++;
                a.APIDTarget = Quaternion.Identity;   // writes reach a prim whose backend is gone
                a.APIDActive = true;
            });
        Assert.True(backendGone);
        Assert.True(shapesBefore > 0);
        Assert.Equal(0, shapesAfter);
        Assert.True(framesAfter > 0, "the run did not go on after the region was removed");
    }
}
