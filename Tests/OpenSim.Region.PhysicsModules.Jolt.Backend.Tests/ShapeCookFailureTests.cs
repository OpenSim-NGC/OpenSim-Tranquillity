/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// joltc returns nullptr when a hull or mesh cook fails and JoltPhysicsSharp wraps it in
/// a Shape whose Handle is 0. Before the fix that shape was registered and reached native as null. A failed cook
/// must now surface as a managed <see cref="ArgumentException"/> - the ShapeId never exists, so it can never reach
/// CreateBody. None of the failing-cook tests below pass a ShapeId on to CreateBody, red or green.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ShapeCookFailureTests
{
    /// <summary>
    /// Measured: Jolt 5.4.0 does NOT fail this cook - 9 coplanar points build a flat hull. So the
    /// assertion is the invariant this guard protects rather than a throw: the result is either an exception or a
    /// shape CreateBody accepts, never a dead (Handle 0) shape.
    /// </summary>
    [Fact]
    public void Coplanar_hull_never_yields_a_dead_shape()
    {
        using var t = new JoltTestBackend();
        var pts = new List<Vector3>();
        for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
                pts.Add(new Vector3(x, y, 0f));   // 9 points on a 1 m grid at z = 0
        ShapeId hull;
        try { hull = t.B.CreateConvexHullShape(pts.ToArray()); }
        catch (ArgumentException) { return; }
        var d = BodyDesc.Default;
        d.Shape = hull;
        d.Position = new Vector3(128f, 128f, 10f);
        Assert.True(t.B.IsBodyValid(t.B.CreateBody(d)));
    }

    [Fact]
    public void Coincident_hull_throws()
    {
        using var t = new JoltTestBackend();
        var pts = Enumerable.Repeat(new Vector3(1f, 2f, 3f), 6).ToArray();
        Assert.Throws<ArgumentException>(() => t.B.CreateConvexHullShape(pts));
    }

    [Fact]
    public void Collinear_hull_throws()
    {
        using var t = new JoltTestBackend();
        var pts = Enumerable.Range(0, 5).Select(i => new Vector3(i, 0f, 0f)).ToArray();
        Assert.Throws<ArgumentException>(() => t.B.CreateConvexHullShape(pts));
    }

    [Fact]
    public void Hull_with_a_NaN_point_throws()
    {
        using var t = new JoltTestBackend();
        var pts = CubePoints().ToList();
        pts.Add(new Vector3(float.NaN, 0.5f, 0.5f));
        Assert.Throws<ArgumentException>(() => t.B.CreateConvexHullShape(pts.ToArray()));
    }

    [Fact]
    public void Mesh_with_only_degenerate_triangles_throws()
    {
        using var t = new JoltTestBackend();
        var verts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        // Every triangle repeats an index, so Sanitize leaves none.
        var idx = new[] { 0, 0, 1, 1, 2, 2, 2, 0, 0 };
        Assert.Throws<ArgumentException>(() => t.B.CreateMeshShape(verts, idx));
    }

    /// <summary>EXCLUDED from the red run: before the fix this index is read out of bounds inside native Sanitize.</summary>
    [Fact]
    public void Mesh_with_an_out_of_range_index_throws()
    {
        using var t = new JoltTestBackend();
        var verts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
        var idx = new[] { 0, 1, 2, 0, 1, 1000 };
        Assert.Throws<ArgumentException>(() => t.B.CreateMeshShape(verts, idx));
        Assert.Throws<ArgumentException>(() => t.B.CreateMeshShape(verts, new[] { 0, 1, -1 }));
    }

    [Fact]
    public void Cube_hull_body_steps_ten_frames_with_finite_state()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        var hull = t.B.CreateConvexHullShape(CubePoints());
        var body = t.Dynamic(hull, new Vector3(128f, 128f, 5f));
        Assert.True(t.B.IsBodyValid(body));
        for (var i = 0; i < 10; i++)
            t.Step();
        Assert.True(t.B.TryGetBodyState(body, out var s));
        Assert.True(JoltTestBackend.Finite(in s), $"state went non-finite: {s.Position} {s.LinearVelocity}");
        Assert.True(s.Position.Z < 5f, "the hull body should have fallen");
    }

    [Fact]
    public void Compound_of_two_boxes_creates_a_body()
    {
        using var t = new JoltTestBackend();
        var box = t.B.CreateBoxShape(new Vector3(0.5f));
        var compound = t.B.CreateCompoundShape(new[]
        {
            new CompoundChild { Shape = box, Position = Vector3.Zero, Orientation = Quaternion.Identity, UserData = 1 },
            new CompoundChild { Shape = box, Position = new Vector3(1.2f, 0, 0), Orientation = Quaternion.Identity, UserData = 2 },
        });
        var body = t.Dynamic(compound, new Vector3(128f, 128f, 10f));
        Assert.True(t.B.IsBodyValid(body));
        t.Step();
        Assert.True(t.B.TryGetBodyState(body, out var s) && JoltTestBackend.Finite(in s));
    }

    internal static Vector3[] CubePoints()
    {
        var pts = new List<Vector3>();
        for (var i = 0; i < 8; i++)
            pts.Add(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f));
        return pts.ToArray();
    }
}
