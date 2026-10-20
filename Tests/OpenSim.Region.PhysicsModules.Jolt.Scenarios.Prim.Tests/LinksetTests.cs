/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical linkset through every change core makes to its parts, through the harness at the 11 Hz heartbeat, with
/// [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. Each test calls the prims' actors in the order core
/// calls them (SceneObjectGroup and SceneObjectPart): link() after a part turns physical, then every child's region place
/// again; RemovePrim, never delink(), when a part is unlinked, set to PRIM_PHYSICS_SHAPE_TYPE NONE or deleted; Size, Shape,
/// Position and Orientation on the part itself when it is edited; and root first, then each child, when the object is
/// turned physical or not, with delink() after a part turns non-physical. Afterwards the linkset is one body, with the
/// summed mass of its parts, resting where it rested, and the engine holds no shape it did not hold before. A ray reports
/// the part it hit: llCastRay's RC_GET_ROOT_KEY replaces the hit's uuid with its object's root and RC_GET_LINK_NUM adds
/// the number of the link that was hit (wiki.secondlife.com/wiki/LlCastRay). Each part's own density
/// places the linkset's centre of mass (the wiki's Physics Material Settings test page allows different physics settings
/// for each prim in a linked set). Serial with the other native tests: every run steps a real backend on the shared job
/// pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class LinksetTests
{
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private const float X = 128f, Y = 128f;
    private const uint RootId = 1301, LeftId = 1302, RightId = 1303, NewId = 1304;
    private const float CubeMass = 10f;            // 1 m^3 x 1000 kg/m^3 / 100
    private const double SettleSeconds = 2.0;      // a dropped linkset is at rest by then
    private const double AfterSeconds = 3.0;       // how long it is watched after the change

    public enum Change { LinkIn, Unlink, ResizeChild, ResizeRoot, MoveChild, TurnChild, ReshapeChild, ShapeNoneAndBack, DeletePart, PhysicsOffAndOn }

    // Three 1 m cubes in a row along x, resting on the ground: the root in the middle.
    private sealed class Row
    {
        public PhysicsActor Root, Left, Right;
        public readonly List<PhysicsActor> Parts = new();
        public int TerrainBodies;
    }

    private static Row AddRow(Run r, float lift = 0.01f, float rightDensity = 1000f)
    {
        float z = r.GroundAt(X, Y) + 0.5f + lift;
        var row = new Row { TerrainBodies = r.Capacity.LiveBodyCount };
        row.Root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
        row.Left = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X - 1f, Y, z), Quaternion.Identity, true, LeftId, row.Root);
        row.Right = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X + 1f, Y, z), Quaternion.Identity, true, RightId, row.Root, rightDensity);
        row.Parts.AddRange(new[] { row.Root, row.Left, row.Right });
        return row;
    }

    private static void Run(double physicsHz, double seconds, Action<Run> setup, Action<Run> act)
    {
        var sc = new Scenario
        {
            Name = "linkset",
            DefaultDuration = _ => (float)seconds,
            Setup = setup,
            Input = act,
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
    }

    // Core's ResetChildPrimPhysicsPositions after a link: every child is handed its region place again.
    private static void ResetChildPlaces(Row row)
    {
        foreach (PhysicsActor p in row.Parts)
        {
            if (p == row.Root)
                continue;
            p.Position = p.Position;
            p.Orientation = p.Orientation;
        }
    }

    // A part re-added to physics as core adds one (AddToPhysics: AddPrimShape, its density, link() when physical).
    private static PhysicsActor ReAdd(Run r, Row row, PhysicsActor old, uint id, PrimitiveBaseShape shape, Vector3 size)
    {
        Vector3 pos = old.Position;
        Quaternion rot = old.Orientation;
        r.PhysicsScene.RemovePrim(old);
        row.Parts.Remove(old);
        PhysicsActor pa = r.AddPart(shape, size, pos, rot, true, id, row.Root);
        row.Parts.Add(pa);
        return pa;
    }

    private static void Apply(Change change, Run r, Row row)
    {
        switch (change)
        {
            case Change.LinkIn:
            {
                // A physical prim joins: it turns physical (it is), link() twice (SceneObjectPart.DoPhysicsPropertyUpdate and
                // SceneObjectGroup.LinkToGroup), then every child's place again.
                PhysicsActor n = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, row.Right.Position + new Vector3(1f, 0f, 0f), row.Root.Orientation, true, NewId);
                n.link(row.Root);
                n.link(row.Root);
                row.Parts.Add(n);
                ResetChildPlaces(row);
                break;
            }
            case Change.Unlink:
            {
                // SceneObjectGroup.DelinkFromGroup: RemovePrim, then the part as an object of its own.
                Vector3 pos = row.Right.Position;
                Quaternion rot = row.Right.Orientation;
                r.PhysicsScene.RemovePrim(row.Right);
                row.Parts.Remove(row.Right);
                r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, pos, rot, true, RightId);
                break;
            }
            case Change.ResizeChild:
                row.Right.Size = new Vector3(1f, 1.5f, 1f);
                break;
            case Change.ResizeRoot:
                row.Root.Size = new Vector3(1f, 1.5f, 1f);
                break;
            case Change.MoveChild:
                // Edit linked parts: the child's OffsetPosition, which hands its actor its region position and rotation.
                row.Right.Position = row.Right.Position + new Vector3(0f, 0.5f, 0f);
                row.Right.Orientation = row.Right.Orientation;
                break;
            case Change.TurnChild:
                row.Right.Orientation = row.Right.Orientation * Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 4f);
                break;
            case Change.ReshapeChild:
                // A 1 m box becomes a 1 m sphere (the harness has no mesher, so a sphere is the reshape that cooks to its own
                // shape rather than a bounding box).
                row.Right.Shape = PrimitiveBaseShape.CreateSphere();
                break;
            case Change.ShapeNoneAndBack:
                // NONE: RemoveFromPhysics. Back: AddToPhysics and link(). Done together here; the rebuild sees both.
                row.Right = ReAdd(r, row, row.Right, RightId, PrimitiveBaseShape.CreateBox(), Unit);
                break;
            case Change.DeletePart:
                r.PhysicsScene.RemovePrim(row.Right);
                row.Parts.Remove(row.Right);
                break;
            case Change.PhysicsOffAndOn:
                foreach (PhysicsActor p in row.Parts) { p.IsPhysical = false; p.delink(); }
                foreach (PhysicsActor p in row.Parts)
                {
                    p.IsPhysical = true;
                    if (p != row.Root)
                        p.link(row.Root);
                }
                break;
        }
    }

    // The expected mass of the linkset afterwards: core adds up every part's (SceneObjectGroup.GetMass).
    private static float SumOfParts(Row row)
    {
        float m = 0f;
        foreach (PhysicsActor p in row.Parts)
            m += p.Mass;
        return m;
    }

    private static int BodiesOf(Row row)
    {
        int n = 0;
        foreach (PhysicsActor p in row.Parts)
            if (((JoltPrim)p).BodyHandle.IsValid)
                n++;
        return n;
    }

    [Theory]
    [InlineData(Change.LinkIn, 45.0)]
    [InlineData(Change.LinkIn, 0.0)]
    [InlineData(Change.Unlink, 45.0)]
    [InlineData(Change.Unlink, 0.0)]
    [InlineData(Change.ResizeChild, 45.0)]
    [InlineData(Change.ResizeChild, 0.0)]
    [InlineData(Change.ResizeRoot, 45.0)]
    [InlineData(Change.ResizeRoot, 0.0)]
    [InlineData(Change.MoveChild, 45.0)]
    [InlineData(Change.MoveChild, 0.0)]
    [InlineData(Change.TurnChild, 45.0)]
    [InlineData(Change.TurnChild, 0.0)]
    [InlineData(Change.ReshapeChild, 45.0)]
    [InlineData(Change.ReshapeChild, 0.0)]
    [InlineData(Change.ShapeNoneAndBack, 45.0)]
    [InlineData(Change.ShapeNoneAndBack, 0.0)]
    [InlineData(Change.DeletePart, 45.0)]
    [InlineData(Change.DeletePart, 0.0)]
    [InlineData(Change.PhysicsOffAndOn, 45.0)]
    [InlineData(Change.PhysicsOffAndOn, 0.0)]
    public void A_resting_linkset_stays_one_body_at_rest_with_its_parts_mass(Change change, double physicsHz)
    {
        Row row = null;
        bool changed = false;
        Vector3 rested = Vector3.Zero;
        float maxRise = 0f, maxSlide = 0f, endSpeed = 0f, bodyMass = 0f, partsMass = 0f;
        int bodies = -1, liveBodies = -1, unlinkedBodies = 0;
        Run(physicsHz, SettleSeconds + AfterSeconds,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                if (!changed)
                {
                    changed = true;
                    rested = row.Root.Position;
                    Apply(change, r, row);
                    return;
                }
                Vector3 p = row.Root.Position;
                maxRise = MathF.Max(maxRise, p.Z - rested.Z);
                maxSlide = MathF.Max(maxSlide, MathF.Sqrt((p.X - rested.X) * (p.X - rested.X) + (p.Y - rested.Y) * (p.Y - rested.Y)));
                endSpeed = row.Root.Velocity.Length();
                bodies = BodiesOf(row);
                liveBodies = r.Capacity.LiveBodyCount - row.TerrainBodies;
                bodyMass = ((JoltPrim)row.Root).BodyMass;
                partsMass = SumOfParts(row);
                unlinkedBodies = change == Change.Unlink ? 1 : 0;
            });

        Assert.Equal(1, bodies);
        Assert.Equal(1 + unlinkedBodies, liveBodies);
        Assert.Equal(partsMass, bodyMass, 2);
        Assert.True(maxRise <= 0.01f, $"rose {maxRise * 1000f:0.#} mm after {change}");
        Assert.True(maxSlide <= 0.01f, $"slid {maxSlide * 1000f:0.#} mm after {change}");
        Assert.True(endSpeed < 0.01f, $"still moving at {endSpeed:0.###} m/s after {change}");
    }

    // The mass after a part's resize or reshape, worked out by hand: the resized cube is 1.5 m^3, the 1 m sphere
    // 4/3 x pi x 0.5^3 m^3, at 10 per m^3.
    [Theory]
    [InlineData(Change.ResizeChild, 45.0, 2f * CubeMass + 1.5f * CubeMass)]
    [InlineData(Change.ResizeChild, 0.0, 2f * CubeMass + 1.5f * CubeMass)]
    [InlineData(Change.ReshapeChild, 45.0, 2f * CubeMass + 4f / 3f * MathF.PI * 0.125f * CubeMass)]
    [InlineData(Change.ReshapeChild, 0.0, 2f * CubeMass + 4f / 3f * MathF.PI * 0.125f * CubeMass)]
    public void A_resized_or_reshaped_part_counts_with_its_new_volume(Change change, double physicsHz, float expected)
    {
        Row row = null;
        bool changed = false;
        float mass = 0f;
        Run(physicsHz, SettleSeconds + 1.0,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                if (!changed) { changed = true; Apply(change, r, row); return; }
                mass = ((JoltPrim)row.Root).BodyMass;
            });

        Assert.Equal(expected, mass, 2);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Resizing_a_part_100_times_leaves_the_native_shape_count_where_it_was(double physicsHz)
    {
        Row row = null;
        int before = -1, after = -1, resizes = 0, bodies = -1;
        Run(physicsHz, SettleSeconds + 110 / 11.0,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                if (before < 0)
                {
                    before = r.Capacity.LiveShapeCount;
                    return;
                }
                if (resizes < 100)
                {
                    resizes++;
                    row.Right.Size = resizes % 2 == 1 ? new Vector3(1f, 1.2f, 1f) : Unit;
                    return;
                }
                after = r.Capacity.LiveShapeCount;
                bodies = BodiesOf(row);
            });

        Assert.Equal(100, resizes);
        Assert.Equal(before, after);
        Assert.Equal(1, bodies);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_unlinked_into_three_prims_is_three_bodies_at_rest(double physicsHz)
    {
        Row row = null;
        bool changed = false;
        var parts = new PhysicsActor[3];
        var rested = new Vector3[3];
        float maxMove = 0f, maxSpeed = 0f;
        int bodies = -1, liveBodies = -1;
        Run(physicsHz, SettleSeconds + AfterSeconds,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                if (!changed)
                {
                    changed = true;
                    // SceneObjectGroup.DelinkFromGroup for each child: RemovePrim, then the part as an object of its own.
                    parts[0] = row.Root;
                    uint[] ids = { LeftId, RightId };
                    PhysicsActor[] children = { row.Left, row.Right };
                    for (int i = 0; i < 2; i++)
                    {
                        Vector3 pos = children[i].Position;
                        Quaternion rot = children[i].Orientation;
                        r.PhysicsScene.RemovePrim(children[i]);
                        parts[i + 1] = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, pos, rot, true, ids[i]);
                    }
                    for (int i = 0; i < 3; i++)
                        rested[i] = parts[i].Position;
                    return;
                }
                maxMove = 0f;
                maxSpeed = 0f;
                bodies = 0;
                for (int i = 0; i < 3; i++)
                {
                    maxMove = MathF.Max(maxMove, Vector3.Distance(parts[i].Position, rested[i]));
                    maxSpeed = MathF.Max(maxSpeed, parts[i].Velocity.Length());
                    if (((JoltPrim)parts[i]).BodyHandle.IsValid)
                        bodies++;
                    Assert.Equal(CubeMass, ((JoltPrim)parts[i]).BodyMass, 3);
                }
                liveBodies = r.Capacity.LiveBodyCount - row.TerrainBodies;
            });

        Assert.Equal(3, bodies);
        Assert.Equal(3, liveBodies);
        Assert.True(maxMove <= 0.01f, $"a part moved {maxMove * 1000f:0.#} mm");
        Assert.True(maxSpeed < 0.01f, $"a part still moves at {maxSpeed:0.###} m/s");
    }

    // A ray straight down onto each part of a resting three-prim linkset reports that part, on both of the module's
    // llCastRay entry points (YEngine's filtered one and Phlox's).
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_ray_reports_the_part_it_hits(double physicsHz)
    {
        Row row = null;
        var filtered = new uint[3];
        var plain = new uint[3];
        bool cast = false;
        Run(physicsHz, SettleSeconds + 0.5,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds || cast)
                    return;
                cast = true;
                float[] xs = { X, X - 1f, X + 1f };
                for (int i = 0; i < 3; i++)
                {
                    var from = new Vector3(xs[i], Y, r.GroundAt(X, Y) + 5f);
                    var hits = (List<ContactResult>)r.PhysicsScene.RaycastWorld(from, -Vector3.UnitZ, 10f, 1, RayFilterFlags.AllPrims | RayFilterFlags.land);
                    filtered[i] = hits.Count > 0 ? hits[0].ConsumerID : 0;
                    List<ContactResult> all = r.PhysicsScene.RaycastWorld(from, -Vector3.UnitZ, 10f, 1);
                    plain[i] = all.Count > 0 ? all[0].ConsumerID : 0;
                }
            });

        Assert.True(cast);
        Assert.Equal(new[] { RootId, LeftId, RightId }, filtered);
        Assert.Equal(new[] { RootId, LeftId, RightId }, plain);
    }

    // A part moved or turned within its linkset collides where it now is. The right cube is moved 0.5 m along y, or turned
    // 45 degrees about the vertical; a ray down through a point the part covers only after the change (0.75 m along y
    // from its old centre, or 0.65 m along x, inside the turned square's corner) hits it.
    [Theory]
    [InlineData(Change.MoveChild, 45.0)]
    [InlineData(Change.MoveChild, 0.0)]
    [InlineData(Change.TurnChild, 45.0)]
    [InlineData(Change.TurnChild, 0.0)]
    public void A_part_moved_or_turned_in_its_linkset_collides_where_it_now_is(Change change, double physicsHz)
    {
        Row row = null;
        bool changed = false;
        uint before = 1, after = 0;
        Vector3 probe = change == Change.MoveChild ? new Vector3(X + 1f, Y + 0.75f, 0f) : new Vector3(X + 1.65f, Y, 0f);
        Run(physicsHz, SettleSeconds + 1.0,
            r => row = AddRow(r),
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                var from = new Vector3(probe.X, probe.Y, r.GroundAt(X, Y) + 5f);
                var hits = (List<ContactResult>)r.PhysicsScene.RaycastWorld(from, -Vector3.UnitZ, 10f, 1, RayFilterFlags.AllPrims | RayFilterFlags.land);
                uint id = hits.Count > 0 ? hits[0].ConsumerID : uint.MaxValue;
                if (!changed)
                {
                    changed = true;
                    before = id;
                    Apply(change, r, row);
                    return;
                }
                after = id;
            });

        Assert.Equal(0u, before);         // the terrain: the part did not cover the point yet
        Assert.Equal(RightId, after);
    }

    // A linkset dropped 1 m lands, and is then turned non-physical: each part's static body is where the part is now, not
    // where it was when the linkset was welded. A ray down onto each part meets its top 1 m above the ground.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_turned_non_physical_after_it_moved_leaves_each_part_where_it_is(double physicsHz)
    {
        Row row = null;
        bool changed = false;
        var tops = new float[3];
        var ids = new uint[3];
        int bodies = -1;
        float ground = 0f;
        Run(physicsHz, SettleSeconds + 1.0,
            r => { row = AddRow(r, lift: 1f); ground = r.GroundAt(X, Y); },
            r =>
            {
                if (r.Now < SettleSeconds)
                    return;
                if (!changed)
                {
                    changed = true;
                    // SceneObjectGroup.UpdateFlags: the root, then each child; delink() after each turns non-physical.
                    foreach (PhysicsActor p in row.Parts) { p.IsPhysical = false; p.delink(); }
                    return;
                }
                bodies = BodiesOf(row);
                float[] xs = { X, X - 1f, X + 1f };
                for (int i = 0; i < 3; i++)
                {
                    var hits = (List<ContactResult>)r.PhysicsScene.RaycastWorld(new Vector3(xs[i], Y, ground + 5f), -Vector3.UnitZ, 10f, 1,
                                                                                RayFilterFlags.AllPrims | RayFilterFlags.land);
                    tops[i] = hits.Count > 0 ? hits[0].Pos.Z - ground : float.NaN;
                    ids[i] = hits.Count > 0 ? hits[0].ConsumerID : 0;
                }
            });

        Assert.Equal(3, bodies);
        Assert.Equal(new[] { RootId, LeftId, RightId }, ids);
        foreach (float top in tops)
            Assert.True(MathF.Abs(top - 1f) <= 0.03f, $"a part's top is {top:0.###} m above the ground, not 1 m");
    }

    // Each part's density places the centre of mass. The row floats (buoyancy 1) with its right cube 8 times as dense:
    // masses 10, 10 and 80, so the centre of mass is 0.7 m right of the root's centre. Turned by a torque about the
    // vertical, the linkset turns about that point, which stays where it is, and the root reports it.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_heavy_part_moves_the_centre_of_mass_and_a_torque_turns_the_linkset_about_it(double physicsHz)
    {
        Row row = null;
        bool started = false;
        Vector3 com0 = Vector3.Zero, reported = Vector3.Zero;
        float maxDrift = 0f, turned = 0f, mass = 0f;
        Run(physicsHz, 2.5,
            r =>
            {
                row = AddRow(r, lift: 20f, rightDensity: 8000f);
                row.Root.Buoyancy = 1f;
            },
            r =>
            {
                if (r.Now < 0.5)
                    return;
                if (!started)
                {
                    started = true;
                    com0 = new Vector3(X + 0.7f, Y, row.Root.Position.Z);
                    reported = row.Root.CenterOfMass;
                    mass = ((JoltPrim)row.Root).BodyMass;
                    row.Root.Torque = new Vector3(0f, 0f, 60f);
                    return;
                }
                if (r.Now >= 1.5)
                    row.Root.Torque = Vector3.Zero;
                // The point 0.7 m along the root's x axis, where the centre of mass is on the object.
                Vector3 com = row.Root.Position + new Vector3(0.7f, 0f, 0f) * row.Root.Orientation;
                maxDrift = MathF.Max(maxDrift, Vector3.Distance(com, com0));
                Vector3 ax = new Vector3(1f, 0f, 0f) * row.Root.Orientation;
                turned = MathF.Atan2(ax.Y, ax.X);
            });

        Assert.Equal(100f, mass, 2);
        Assert.True(Vector3.Distance(reported, com0) <= 0.005f, $"llGetCenterOfMass gave {reported}, expected {com0}");
        Assert.True(turned > 0.5f, $"turned only {turned:0.###} rad");
        Assert.True(maxDrift <= 0.01f, $"the centre of mass moved {maxDrift * 1000f:0.#} mm while the linkset turned");
    }
}
