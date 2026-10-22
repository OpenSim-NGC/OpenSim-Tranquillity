/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a physical mesh's hull list changes and costs: the trough and the gateway with and without a hull list side by
/// side, the mass, centre of mass and inertia the new shape gives, and the cook time and per-step cost for 1, 8, 32 and
/// 256 hulls. Each result is a line "MESH-HULLS ..." in the test output; the assertions are sanity checks, not timings.
/// Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class MeshHullListFiguresTests
{
    private readonly ITestOutputHelper _out;
    public MeshHullListFiguresTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static byte[] WithList(Box[] boxes) => Asset(Soup(boxes), boxes, false);
    private static byte[] WithoutList(Box[] boxes) => Asset(Soup(boxes), boxes, true);

    // ------------------------------------------------------------------ the recon's figures, with the list beside them

    [Fact]
    public void The_trough_and_the_gateway_with_and_without_a_hull_list()
    {
        string scratch = Scratch();
        var lines = new List<string>();
        foreach (bool list in new[] { true, false })
        {
            float rest, boxPast, avPast;
            string kind;
            JoltScene s1 = NewScene(scratch);
            try
            {
                AddMesh(s1, list ? WithList(Trough) : WithoutList(Trough), TroughHeight, true, Prim, out kind);
                Step(s1, 2f);
                rest = DropBall(s1, TroughHeight);
            }
            finally { s1.Dispose(); }
            JoltScene s2 = NewScene(scratch);
            try
            {
                AddMesh(s2, list ? WithList(Gateway) : WithoutList(Gateway), GatewayHeight, true, Prim, out _);
                Step(s2, 2f);
                boxPast = FireBox(s2);
            }
            finally { s2.Dispose(); }
            JoltScene s3 = NewScene(scratch);
            try
            {
                AddMesh(s3, list ? WithList(Gateway) : WithoutList(Gateway), GatewayHeight, true, Prim, out _);
                Step(s3, 2f);
                avPast = WalkAvatar(s3);
            }
            finally { s3.Dispose(); }
            lines.Add($"MESH-HULLS physical PRIM {(list ? "with hull list" : "no hull list")}: cooked={kind} ball_rest_z={F(rest)} box_past_far_side={F(boxPast)} avatar_past_far_side={F(avPast)}");
            if (list)
            {
                Assert.InRange(rest, TroughFloorTop + 0.1f, TroughFloorTop + 0.3f);
                Assert.True(boxPast > 0f && avPast > 0f);
            }
            else
            {
                Assert.InRange(rest, TroughHeight, TroughHeight + 0.4f);   // as before: on top of the one hull
                Assert.True(boxPast < 0f && avPast < 0f);
            }
        }
        lines.Add("MESH-HULLS recon (before): physical = one hull: ball 1.68, box -2.25, avatar -2.25; the hull list's own answer: ball 0.48, box +1.23, avatar +9.06");
        foreach (string l in lines) Say(l);
    }

    // ------------------------------------------------------------------ mass, centre of mass and inertia

    [Fact]
    public void Mass_centre_of_mass_and_inertia_of_the_new_shape()
    {
        string scratch = Scratch();
        var lines = new List<string>();
        float troughList = 0f, troughHull = 0f;
        foreach ((string name, Box[] boxes, float height) in new[] { ("trough", Trough, TroughHeight), ("gateway", Gateway, GatewayHeight) })
            foreach (bool list in new[] { true, false })
            {
                JoltScene scene = NewScene(scratch);
                try
                {
                    PhysicsActor a = AddMesh(scene, list ? WithList(boxes) : WithoutList(boxes), height, true, Prim, out string kind);
                    var prim = (JoltPrim)a;
                    IPhysicsBackend backend = scene.Backend;
                    float volume = backend.GetShapeVolume(prim.ShapeHandle);
                    var com = backend.GetShapeCenterOfMass(prim.ShapeHandle);
                    var inertia = backend.GetBodyInertiaDiagonal(prim.BodyHandle);
                    float mass = prim.BodyMass;
                    lines.Add($"MESH-HULLS mass {name} {(list ? "hull list" : "one hull ")}: kind={kind} volume={volume:0.000} m3 body_mass={mass:0.0} " +
                              $"com_in_shape=({com.X:0.000},{com.Y:0.000},{com.Z:0.000}) inertia_diag=({inertia.X:0.0},{inertia.Y:0.0},{inertia.Z:0.0})");
                    Assert.True(mass > 0f && float.IsFinite(mass));
                    if (name == "trough") { if (list) troughList = mass; else troughHull = mass; }
                }
                finally { scene.Dispose(); }
            }
        // The hulls are the three boxes (3.84 m3); the one hull is the box round them (12 m3).
        Assert.True(troughList < troughHull * 0.5f, $"{troughList} vs {troughHull}");
        foreach (string l in lines) Say(l);
    }

    // ------------------------------------------------------------------ cost by hull count

    // n small boxes in a grid, 0.1 m each: one hull each, and a display mesh that is their soup.
    private static Box[] Grid(int n)
    {
        var boxes = new Box[n];
        for (int i = 0; i < n; i++)
        {
            float x = (i % 16) * 0.2f - 1.6f, y = (i / 16) * 0.2f - 1.6f;
            boxes[i] = new Box(x, x + 0.1f, y, y + 0.1f, -0.05f, 0.05f);
        }
        return boxes;
    }

    [Fact]
    public void Cook_time_and_step_cost_by_hull_count()
    {
        string scratch = Scratch();
        var lines = new List<string>();
        foreach (int hulls in new[] { 1, 8, 32, JoltScene.MaxAssetHulls })
        {
            byte[] asset = WithList(Grid(hulls));
            // Cook time: the first call decodes the asset and cooks; the second finds the mesh in the mesher's cache and cooks.
            JoltScene scene = NewScene(scratch);
            double decodeAndCook, cookOnly;
            try
            {
                PrimitiveBaseShape pbs = MeshPbs(asset);
                var sw = Stopwatch.StartNew();
                ShapeId a = scene.CookPrimShape(scene.Backend, pbs, Vector3.One, true, out _, out string kind, true, true, false);
                decodeAndCook = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                ShapeId b = scene.CookPrimShape(scene.Backend, pbs, Vector3.One, true, out _, out _, true, true, false);
                cookOnly = sw.Elapsed.TotalMilliseconds;
                scene.Backend.ReleaseShape(a);
                scene.Backend.ReleaseShape(b);
                Assert.StartsWith("hull", kind);
            }
            finally { scene.Dispose(); }

            foreach (int bodies in new[] { 1, 50 })
            {
                JoltScene s = NewScene(scratch);
                try
                {
                    UUID id = UUID.Random();
                    var prims = new List<PhysicsActor>();
                    for (int i = 0; i < bodies; i++)
                    {
                        PrimitiveBaseShape p = MeshPbs(asset);
                        p.SculptTexture = id;
                        prims.Add(s.AddPrimShape("m", p, new Vector3(20f + (i % 8) * 6f, 20f + (i / 8) * 6f, Ground + 0.5f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++));
                    }
                    Step(s, 1f);
                    // Keep every body awake and touching the ground: spin it each step (a resting body sleeps and costs nothing).
                    const int steps = 100;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < steps; i++)
                    {
                        foreach (PhysicsActor a in prims) a.RotationalVelocity = new Vector3(0f, 0f, 1f);
                        s.Simulate(Heartbeat);
                    }
                    double perStep = sw.Elapsed.TotalMilliseconds / steps;
                    lines.Add($"MESH-HULLS cost hulls={hulls,3} bodies={bodies,2}: decode+cook={decodeAndCook:0.00} ms cook_only={cookOnly:0.00} ms step={perStep:0.000} ms/step shapes_cooked={s.MeshShapesCooked}");
                    Assert.Equal(1, s.MeshShapesCooked);
                }
                finally { s.Dispose(); }
            }
        }
        foreach (string l in lines) Say(l);
    }
}
