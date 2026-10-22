/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical torus prim keeps its hole. The prim mesher builds a torus as a ring around the prim's local x axis (it
/// turns the circular path about x, PrimMesher.ExtrudeCircular), so its hole runs along x and the ring lies in the y-z
/// plane. The default torus (hole size 1.0 by 0.25, 4 m across here) is 4 m long along x, 4 m across, with a 2 m bore;
/// the same with a hollow of 0.5. A small sphere sent along x passes through the bore, one sent across the axis is
/// stopped by the wall, and an avatar walked at the ring is stopped by it. Each figure is a line "TORUS ..." in the
/// test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicalTorusHoleTests
{
    private readonly ITestOutputHelper _out;
    public PhysicalTorusHoleTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static readonly Vector3 TorusSize = new(4f, 4f, 4f);

    private static PrimitiveBaseShape Torus(float hollow)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.PathCurve = (byte)Extrusion.Curve1;
        pbs.ProfileShape = ProfileShape.Circle;
        pbs.HollowShape = HollowShape.Same;
        pbs.ProfileEnd = 0;
        pbs.ProfileHollow = (ushort)MathF.Round(hollow * 50000f);
        pbs.PathScaleX = 100;
        pbs.PathScaleY = 175;   // hole size <1.0, 0.25>
        pbs.SculptEntry = false;
        return pbs;
    }

    // The torus rests on its rim on the ground, put there so it does not have to fall and roll first.
    private JoltScene SceneWithTorus(float hollow, bool everyBeat, out PhysicsActor torus, out string kind)
    {
        JoltScene scene = NewScene(Scratch(), ("PhysicsStepRate", everyBeat ? "0" : "45"));
        torus = scene.AddPrimShape("torus", Torus(hollow), new Vector3(CentreX, CentreY, Ground + 2f), TorusSize, Quaternion.Identity, true, _id++);
        kind = (string)typeof(JoltPrim).GetField("_shapeKind", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(torus)!;
        Step(scene, 1f);
        return scene;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (float hollow in new[] { 0f, 0.5f })
            foreach (bool everyBeat in new[] { false, true })
                yield return new object[] { hollow, everyBeat };
    }

    // A 0.3 m sphere sent along +x from 3.5 m before the torus, at the height of its middle.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_small_sphere_sent_along_the_axis_passes_through_the_hole(float hollow, bool everyBeat)
    {
        JoltScene scene = SceneWithTorus(hollow, everyBeat, out PhysicsActor torus, out string kind);
        try
        {
            Vector3 c = torus.Position;
            PhysicsActor ball = scene.AddPrimShape("ball", PrimitiveBaseShape.CreateSphere(), new Vector3(c.X - 3.5f, c.Y, c.Z),
                                                   new Vector3(0.3f, 0.3f, 0.3f), Quaternion.Identity, true, _id++);
            Step(scene, Heartbeat);
            ball.Velocity = new Vector3(6f, 0f, 0f);
            Step(scene, 3f);
            float past = ball.Position.X - c.X;
            Say($"TORUS hollow={hollow} steps={(everyBeat ? "11Hz" : "45Hz")} cooked={kind} torus_centre_z={F(c.Z - Ground)} ball_x_past_centre={F(past)}");
            Assert.True(past > 2.5f, $"the sphere got {past} m past the torus centre");   // through the 4 m long bore and out
        }
        finally { scene.Dispose(); }
    }

    // The same sphere sent along +y, across the axis: the ring's wall stops it.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_small_sphere_sent_across_the_axis_is_stopped_by_the_wall(float hollow, bool everyBeat)
    {
        JoltScene scene = SceneWithTorus(hollow, everyBeat, out PhysicsActor torus, out string kind);
        try
        {
            Vector3 c = torus.Position;
            PhysicsActor ball = scene.AddPrimShape("ball", PrimitiveBaseShape.CreateSphere(), new Vector3(c.X, c.Y - 3.5f, c.Z),
                                                   new Vector3(0.3f, 0.3f, 0.3f), Quaternion.Identity, true, _id++);
            Step(scene, Heartbeat);
            ball.Velocity = new Vector3(0f, 6f, 0f);
            Step(scene, 3f);
            float past = ball.Position.Y - torus.Position.Y;
            Say($"TORUS hollow={hollow} steps={(everyBeat ? "11Hz" : "45Hz")} cooked={kind} ball_y_past_centre={F(past)}");
            Assert.True(past < 0f, $"the sphere got {past} m past the torus centre");
        }
        finally { scene.Dispose(); }
    }

    // An avatar walked at the ring from outside, across its axis (at the outer wall) and along its axis (at the bore, whose
    // floor is 1 m above the ground).
    [Theory]
    [MemberData(nameof(Cases))]
    public void An_avatar_walked_at_the_ring_does_not_get_through(float hollow, bool everyBeat)
    {
        foreach (bool alongAxis in new[] { false, true })
        {
            JoltScene scene = SceneWithTorus(hollow, everyBeat, out PhysicsActor torus, out _);
            try
            {
                Vector3 c = torus.Position;
                Vector3 dir = alongAxis ? Vector3.UnitX : Vector3.UnitY;
                PhysicsActor av = scene.AddAvatar(_id++, "Test User", new Vector3(c.X, c.Y, Ground + Run.AvatarSize.Z * 0.5f) - dir * 5f, Run.AvatarSize, 0f, false);
                Step(scene, 1f);
                av.TargetVelocity = dir * 3f;
                Step(scene, 6f);
                float past = Vector3.Dot(av.Position - torus.Position, dir);
                Say($"TORUS hollow={hollow} steps={(everyBeat ? "11Hz" : "45Hz")} avatar walked {(alongAxis ? "along the axis" : "across the axis")}: {F(past)} m past the torus centre");
                Assert.True(past < -1.5f, $"{(alongAxis ? "along" : "across")} the axis the avatar got {past} m past the centre");
            }
            finally { scene.Dispose(); }
        }
    }

    // Where the mesher puts the ring: nothing of it lies within a quarter of the size of the x axis.
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void The_meshers_ring_has_a_hole_along_the_x_axis(float hollow)
    {
        var config = new Nini.Config.IniConfigSource();
        config.AddConfig("Startup").Set("DecodedSculptMapPath", Scratch());
        var mesher = new OpenSim.Region.PhysicsModules.Meshing.Meshmerizer();
        mesher.Initialise(config);
        IMesh mesh = mesher.CreateMesh("torus", Torus(hollow), TorusSize, 32f, false, false, false);
        float[] v = mesh.getVertexListAsFloat();
        int[] ix = mesh.getIndexListAsInt();
        float rMin = float.MaxValue, rMax = 0f;
        for (int i = 0; i + 2 < v.Length; i += 3)
        {
            float r = MathF.Sqrt(v[i + 1] * v[i + 1] + v[i + 2] * v[i + 2]);   // distance from the x axis
            rMin = MathF.Min(rMin, r); rMax = MathF.Max(rMax, r);
        }
        // Rays along x near the middle of the bore cross no triangle; rays along z near the middle cross the wall.
        int along = 0, across = 0;
        foreach ((float p, float q) in new[] { (0f, 0f), (0.3f, 0f), (0f, 0.3f), (-0.3f, 0f), (0f, -0.3f) })
            for (int t = 0; t + 2 < ix.Length; t += 3)
            {
                if (Covers(v, ix, t, 1, 2, p, q)) along++;     // the triangle seen along x
                if (Covers(v, ix, t, 0, 1, p, q)) across++;    // the triangle seen along z
            }
        Say($"TORUS-MESH hollow={hollow} vertices={v.Length / 3} distance from the x axis {F(rMin)}..{F(rMax)}; triangles crossed by 5 rays along x: {along}, along z: {across}");
        Assert.True(rMin > 0.9f, $"nearest vertex to the axis is {rMin} m");   // a quarter of the 4 m size
        Assert.Equal(0, along);
        Assert.True(across > 0);
    }

    // Does the triangle at index t, seen along the axis left out by components a and b, cover the point (pa, pb)?
    private static bool Covers(float[] v, int[] ix, int t, int a, int b, float pa, float pb)
    {
        float ax = v[ix[t] * 3 + a], ay = v[ix[t] * 3 + b], bx = v[ix[t + 1] * 3 + a], by = v[ix[t + 1] * 3 + b], cx = v[ix[t + 2] * 3 + a], cy = v[ix[t + 2] * 3 + b];
        if (MathF.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) < 1e-6f)
            return false;   // a triangle with no area covers nothing
        float d1 = (pa - bx) * (ay - by) - (ax - bx) * (pb - by), d2 = (pa - cx) * (by - cy) - (bx - cx) * (pb - cy), d3 = (pa - ax) * (cy - ay) - (cx - ax) * (pb - ay);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }
}
