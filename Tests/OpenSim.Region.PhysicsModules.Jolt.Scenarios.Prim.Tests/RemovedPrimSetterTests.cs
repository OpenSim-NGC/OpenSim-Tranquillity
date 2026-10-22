/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A script that has not heard its object is gone can still set a move-to-target, a hover, a force or torque, or a vehicle
/// type on the prim's actor after the scene removed it. None of these may put the removed prim back on a list the scene
/// keeps and steps every frame, and none may throw. Serial: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RemovedPrimSetterTests
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static int Held(JoltScene scene, string field)
    {
        var set = (HashSet<JoltPrim>)typeof(JoltScene).GetField(field, Private)!.GetValue(scene)!;
        lock (set) return set.Count;
    }

    private static readonly string[] Lists = { "_targeted", "_scriptForced", "_vehicles" };

    /// <summary>Removes a cube from the scene, then calls <paramref name="late"/> on its actor, and runs on.</summary>
    private static (int[] held, int frames) RunRemoved(Action<PhysicsActor> late)
    {
        var held = new int[Lists.Length];
        int frames = 0;
        PhysicsActor actor = null;
        var sc = new Scenario
        {
            Name = "removed-prim-setter",
            DefaultDuration = _ => 2f,
            Setup = r => actor = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, 150f), Quaternion.Identity),
            Input = r =>
            {
                var scene = (JoltScene)r.PhysicsScene;
                if (r.Heartbeats == 5)
                {
                    scene.RemovePrim(actor);
                    late(actor);
                }
                else if (r.Heartbeats > 5)
                {
                    frames++;
                    for (int i = 0; i < Lists.Length; i++) held[i] = Held(scene, Lists[i]);
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        return (held, frames);
    }

    [Fact]
    public void Move_to_target_set_on_a_removed_prim_does_not_put_it_back_on_the_scenes_list()
    {
        (int[] held, int frames) = RunRemoved(a =>
        {
            a.PIDTarget = new Vector3(130f, 128f, 150f);
            a.PIDTau = 1f;
            a.PIDActive = true;
        });
        Assert.True(frames > 0);
        Assert.Equal(0, held[0]);
    }

    [Fact]
    public void Hover_set_on_a_removed_prim_does_not_put_it_back_on_the_scenes_list()
    {
        (int[] held, int frames) = RunRemoved(a =>
        {
            a.PIDHoverHeight = 3f;
            a.PIDHoverType = PIDHoverType.Absolute;
            a.PIDHoverTau = 1f;
            a.PIDHoverActive = true;
        });
        Assert.True(frames > 0);
        Assert.Equal(0, held[0]);
    }

    [Fact]
    public void A_force_or_torque_set_on_a_removed_prim_does_not_put_it_back_on_the_scenes_list()
    {
        (int[] held, int frames) = RunRemoved(a =>
        {
            a.Force = new Vector3(0f, 0f, 10f);
            a.Torque = new Vector3(0f, 1f, 0f);
        });
        Assert.True(frames > 0);
        Assert.Equal(0, held[1]);
    }

    [Fact]
    public void A_vehicle_type_set_on_a_removed_prim_does_not_put_it_back_on_the_scenes_list()
    {
        (int[] held, int frames) = RunRemoved(a => a.VehicleType = 1);
        Assert.True(frames > 0);
        Assert.Equal(0, held[2]);
    }
}
