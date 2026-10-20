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
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How a physical concave prim behaves in a scene, the way a builder would try it: a small ball dropped into a physical
/// bowl settles in the bowl; a small cube in a physical hollow box stays in it, and slides out of an open end when the box is
/// tipped; a ring dropped over a standing cylinder threads onto it and rests at the bottom. Each is run with the prim's
/// shape type PRIM (the cells) and with CONVEX, which must behave as a solid: the ball, the cube and the ring land on top.
/// Every figure is a line "PRIM-BEHAVIOUR ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimHullBehaviourTests
{
    private readonly ITestOutputHelper _out;
    public PrimHullBehaviourTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;

    private static uint _id = 41000;

    private static Spec Named(string name) => Shapes().Single(s => s.Name == name);

    private static PhysicsActor Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, Vector3 at, bool physical,
                                    byte shapeType = Prim, Quaternion? rotation = null)
        => scene.AddPrimShape("prim", pbs, at, size, rotation ?? Quaternion.Identity, physical, false, shapeType, _id++);

    private const float SmallSize = 0.2f;   // the ball and the cube: 0.2 m

    // A place in the middle of the test area, at a height above the ground.
    private static Vector3 At(float height, float x = 0f, float y = 0f) => new(CentreX + x, CentreY + y, Ground + height);

    private static PhysicsActor AddSmall(JoltScene scene, PrimitiveBaseShape pbs, Vector3 at)
        => Add(scene, pbs, new Vector3(SmallSize), at, true);

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ------------------------------------------------------------------ 1: a ball in a bowl

    // The bowl is a hollow half sphere, 1 m across and 0.5 m deep, its wall 0.1 m thick; it is put down open side up, on its
    // rounded side, where it rests with its rim 0.5 m above the ground. A 0.2 m ball is let go 0.15 m above the rim, a little
    // off the bowl's axis.
    //   PRIM:   the ball falls in and rests in the bottom of the bowl, within 0.25 m of the axis, its centre 0.12 to 0.24 m above
    //           the ground (the bowl's floor is 0.1 m up and curves up to the walls, the ball's radius is 0.1 m); the bowl has
    //           not moved by more than 0.05 m.
    //   CONVEX: the bowl is a solid. The ball lands on its flat top: at 0.5 s its centre is 0.55 to 0.65 m above the ground
    //           (the top is 0.5 m up), and in the 4 s it never gets below the top plane, 0.5 m, while it is over the bowl (within
    //           0.45 m of the axis). It may then roll off the tilting dome: that is not asked about.
    [Theory]
    [InlineData(Prim, true)]
    [InlineData(Convex, false)]
    public void A_small_sphere_dropped_into_a_physical_bowl(byte shapeType, bool inside)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Quaternion openUp = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            PhysicsActor bowl = Add(scene, Named("bowl (hollow sphere 0.8, dimple 0..0.5)").Build(), Vector3.One, At(0.505f), true, shapeType, openUp);
            Step(scene, 2f);
            Vector3 bowlAt = bowl.Position;
            float bowlRest = bowlAt.Z - Ground;
            PhysicsActor ball = AddSmall(scene, PrimitiveBaseShape.CreateSphere(), bowlAt + new Vector3(0.03f, -0.02f, 0.5f + 0.15f + SmallSize * 0.5f));
            float landed = 0f, lowestOver = float.MaxValue;
            for (int i = 1; i <= 40; i++)
            {
                Step(scene, 0.1f);
                if (i == 5)
                    landed = ball.Position.Z - Ground;
                if (Horizontal(ball.Position, bowlAt) < 0.45f)
                    lowestOver = MathF.Min(lowestOver, ball.Position.Z - Ground);
            }

            float rest = ball.Position.Z - Ground;
            float off = Horizontal(ball.Position, bowlAt);
            float moved = Vector3.Distance(bowl.Position, bowlAt);
            Say($"PRIM-BEHAVIOUR ball in bowl (type {shapeType}): bowl rests {bowlRest:0.000} m up; ball at 0.5 s {landed:0.000} m up, lowest over the bowl {lowestOver:0.000} m, at 4 s {rest:0.000} m up and {off:0.000} m off the axis; bowl moved {moved:0.000} m");
            Assert.InRange(bowlRest, 0.45f, 0.55f);
            if (inside)
            {
                Assert.InRange(rest, 0.12f, 0.24f);
                Assert.InRange(off, 0f, 0.25f);
                Assert.InRange(moved, 0f, 0.05f);
            }
            else
            {
                Assert.InRange(landed, 0.55f, 0.65f);
                Assert.True(lowestOver >= 0.5f, $"the ball got down to {lowestOver:0.000} m over the bowl");
            }
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: a cube in a hollow box

    // A 1 m hollow box (a square hole, 0.5 m, up and down through it) stands on the ground. A 0.2 m cube is put in the hole,
    // on the ground.
    //   PRIM:   after 3 s it is still in the hole: its centre within 0.16 m of the axis (the hole is 0.25 m either side, the
    //           cube's half is 0.1 m) and 0.08 to 0.13 m up.
    //   CONVEX: the box is a solid; a cube let go 0.15 m above its top lands on it and rests there, 1.05 to 1.15 m up.
    [Theory]
    [InlineData(Prim, true)]
    [InlineData(Convex, false)]
    public void A_small_cube_in_a_physical_hollow_box_stays_in_it(byte shapeType, bool inside)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PhysicsActor box = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, At(0.505f), true, shapeType);
            Step(scene, 1f);
            Vector3 boxAt = box.Position;
            PhysicsActor cube = AddSmall(scene, PrimitiveBaseShape.CreateBox(),
                                         inside ? new Vector3(boxAt.X + 0.04f, boxAt.Y - 0.03f, Ground + 0.11f)
                                                : new Vector3(boxAt.X + 0.04f, boxAt.Y - 0.03f, Ground + 1f + 0.15f + SmallSize * 0.5f));
            Step(scene, 3f);

            float rest = cube.Position.Z - Ground;
            float off = Horizontal(cube.Position, boxAt);
            Say($"PRIM-BEHAVIOUR cube in box (type {shapeType}): cube rests {rest:0.000} m up, {off:0.000} m off the axis");
            if (inside)
            {
                Assert.InRange(rest, 0.08f, 0.13f);
                Assert.InRange(off, 0f, 0.16f);
            }
            else
            {
                Assert.InRange(rest, 1.05f, 1.15f);
                Assert.InRange(off, 0f, 0.4f);
            }
        }
        finally { scene.Dispose(); }
    }

    // The same box, held (selected) 3 m above the ground and turned 45 degrees about X, so its hole slopes and is open at both
    // ends. A 0.2 m cube let go in the middle of the hole lands on the wall below it, slides down along the hole and out of its
    // lower end, and falls to the ground: after 5 s it rests on the ground (centre 0.08 to 0.13 m up), at least 0.3 m
    // sideways of the box's centre, which a cube held by the walls could not be. The box stays where it was held.
    [Fact]
    public void A_small_cube_in_a_hollow_box_slides_out_of_its_open_end_when_the_box_is_tipped()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Quaternion tipped = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f);
            PhysicsActor box = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, At(3f), true, Prim, tipped);
            box.Selected = true;   // held where it is, and tilted: it does not fall
            Step(scene, 0.3f);
            Vector3 boxAt = box.Position;
            PhysicsActor cube = AddSmall(scene, PrimitiveBaseShape.CreateBox(), boxAt);
            Step(scene, 5f);

            float rest = cube.Position.Z - Ground;
            float away = Horizontal(cube.Position, boxAt);
            Say($"PRIM-BEHAVIOUR cube tipped out: box held at {boxAt.Z - Ground:0.000} m, now {box.Position.Z - Ground:0.000} m; the cube rests {rest:0.000} m up, {away:0.000} m off the box's centre");
            Assert.Equal(At(3f).Z, box.Position.Z, 0.05f);
            Assert.InRange(rest, 0.08f, 0.13f);
            Assert.InRange(away, 0.3f, 5f);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: a ring on a pole

    // A ring (a hollow cylinder 1 m across and 0.3 m thick whose hole is 0.8 m across: a washer) lies flat. A static cylinder pole
    // 2 m tall and 60 % as wide as the hole stands in the middle. The ring is let go 0.6 m above the top of the pole, a little off
    // the pole's axis.
    //   PRIM:   the ring slides down the pole and rests flat on the ground around it: its centre 0.13 to 0.18 m up (half its
    //           thickness is 0.15 m) and within 0.1 m of the pole's axis.
    //   CONVEX: the ring is a solid disc: it lands on top of the pole, and in the 5 s the lowest its centre gets while it is over
    //           the pole (within 0.3 m of its axis) is 2.1 to 2.2 m (the pole's top is 2.0 m up, half the ring's thickness 0.15 m).
    //           It may then slip off the thin pole top: that is not asked about.
    // The default torus is not used: the mesher's torus has no hole a 0.1 m ball passes through, even as a static triangle mesh.
    [Theory]
    [InlineData(Prim, true)]
    [InlineData(Convex, false)]
    public void A_ring_dropped_over_a_standing_cylinder_threads_onto_it(byte shapeType, bool threads)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var ring = new Spec { Name = "ring", Profile = ProfileShape.Circle, Hollow = 0.8f };
            Vector3 ringSize = new(1f, 1f, 0.3f);
            const float pole = 0.24f;   // the hole's radius is 0.4 m

            Add(scene, PrimitiveBaseShape.CreateCylinder(), new Vector3(pole * 2f, pole * 2f, 2f), At(1f), false);
            PhysicsActor body = Add(scene, ring.Build(), ringSize, At(2f + 0.6f + 0.15f, 0.02f, -0.01f), true, shapeType);
            float lowestOver = float.MaxValue;
            for (int i = 1; i <= 50; i++)
            {
                Step(scene, 0.1f);
                if (Horizontal(body.Position, At(0f)) < 0.3f)
                    lowestOver = MathF.Min(lowestOver, body.Position.Z - Ground);
            }

            float rest = body.Position.Z - Ground;
            float off = Horizontal(body.Position, At(0f));
            Say($"PRIM-BEHAVIOUR ring on pole (type {shapeType}, {((JoltPrim)body).ShapeKind}): lowest over the pole {lowestOver:0.000} m, at 5 s {rest:0.000} m up and {off:0.000} m off the pole's axis");
            if (threads)
            {
                Assert.InRange(rest, 0.13f, 0.18f);
                Assert.InRange(off, 0f, 0.1f);
            }
            else
            {
                Assert.InRange(lowestOver, 2.1f, 2.2f);
            }
        }
        finally { scene.Dispose(); }
    }
}
