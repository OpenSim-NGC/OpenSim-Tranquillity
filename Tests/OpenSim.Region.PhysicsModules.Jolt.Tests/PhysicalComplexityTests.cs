/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] MaxPhysicalComplexity: the most collision shapes one physical object (the whole linkset) may have. A prim counts 1 if it
/// is a plain box, sphere or cylinder and its hull count (a hollow box 4, a torus 12) if not; a prim of shape type NONE counts 0
/// (Halcyon's rule). An object over the limit raises <see cref="PhysicsActor.OnComplexityError"/> on its root's actor with
/// "(N of M convex hulls)", wherever its count can grow: a prim made physical, a part linked in, a shape or shape type changed,
/// the end of building. 0 (the default) is no limit. The physics cost an actor reports is the same count. Each result is a line
/// "PRIM-COMPLEXITY ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicalComplexityTests
{
    private readonly ITestOutputHelper _out;
    public PhysicalComplexityTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;
    private const byte None = (byte)PhysShapeType.none;

    private static uint _id = 43000;

    private static PrimitiveBaseShape HollowBox() => Shapes().Single(s => s.Name == "hollow box 0.5").Build();   // 4 hulls
    private static PrimitiveBaseShape Torus() => Shapes().Single(s => s.Name == "torus").Build();               // 12 hulls

    private static JoltPrim Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 at, bool physical, byte shapeType = Prim)
        => (JoltPrim)scene.AddPrimShape("prim", pbs, at, Vector3.One, Quaternion.Identity, physical, false, shapeType, _id++);

    private static Vector3 Spot(int n) => new(CentreX + n * 3f, CentreY, Ground + 3f);

    /// <summary>Listens to an actor's complexity errors.</summary>
    private sealed class Heard
    {
        public readonly List<string> Details = new();
        public Heard(PhysicsActor actor) { actor.OnComplexityError += d => Details.Add(d); }
    }

    private static JoltScene SceneWithLimit(int limit) => NewScene(Scratch(), ("MaxPhysicalComplexity", limit.ToString()));

    // ------------------------------------------------------------------ the setting

    [Fact]
    public void The_setting_defaults_to_no_limit_and_reads_a_whole_number()
    {
        var none = new IniConfigSource();
        Assert.Equal(0, JoltConfig.FromConfig(none, new List<string>()).MaxPhysicalComplexity);

        var set = new IniConfigSource();
        set.AddConfig("Jolt").Set("MaxPhysicalComplexity", "256");
        Assert.Equal(256, JoltConfig.FromConfig(set, new List<string>()).MaxPhysicalComplexity);

        var bad = new IniConfigSource();
        bad.AddConfig("Jolt").Set("MaxPhysicalComplexity", "-3");
        var warnings = new List<string>();
        Assert.Equal(0, JoltConfig.FromConfig(bad, warnings).MaxPhysicalComplexity);
        Assert.Contains(warnings, w => w.Contains("MaxPhysicalComplexity", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the count

    [Fact]
    public void A_prim_counts_one_for_a_simple_shape_its_hulls_for_a_cut_one_and_nothing_for_none()
    {
        JoltScene scene = SceneWithLimit(0);
        try
        {
            JoltPrim box = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            JoltPrim sphere = Add(scene, PrimitiveBaseShape.CreateSphere(), Spot(1), true);
            JoltPrim hollow = Add(scene, HollowBox(), Spot(2), true);
            JoltPrim torus = Add(scene, Torus(), Spot(3), true);
            JoltPrim convexHollow = Add(scene, HollowBox(), Spot(4), true, Convex);
            JoltPrim none = Add(scene, HollowBox(), Spot(5), true);
            none.PhysicsShapeType = None;
            JoltPrim idle = Add(scene, HollowBox(), Spot(6), false);
            Say($"PRIM-COMPLEXITY counts: box {box.ComplexityCount}, sphere {sphere.ComplexityCount}, hollow box {hollow.ComplexityCount}, torus {torus.ComplexityCount}, " +
                $"hollow box CONVEX {convexHollow.ComplexityCount}, NONE {none.ComplexityCount}, not physical {idle.ComplexityCount}");
            Assert.Equal(new[] { 1, 1, 4, 12, 1, 0, 1 },
                         new[] { box.ComplexityCount, sphere.ComplexityCount, hollow.ComplexityCount, torus.ComplexityCount, convexHollow.ComplexityCount, none.ComplexityCount, idle.ComplexityCount });
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_object_counts_every_part_of_its_linkset_and_not_the_parts_of_shape_type_none()
    {
        JoltScene scene = SceneWithLimit(0);
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            JoltPrim a = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 1.5f, 0f), true);
            JoltPrim b = Add(scene, Torus(), Spot(0) + new Vector3(0f, 3f, 0f), true);
            JoltPrim c = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 4.5f, 0f), true);
            c.PhysicsShapeType = None;
            foreach (JoltPrim p in new[] { a, b, c })
                p.link(root);
            Say($"PRIM-COMPLEXITY object: {root.ObjectComplexity()} (root 1, hollow box 4, torus 12, NONE 0); seen from a child {a.ObjectComplexity()}");
            Assert.Equal(1 + 4 + 12 + 0, root.ObjectComplexity());
            Assert.Equal(root.ObjectComplexity(), a.ObjectComplexity());
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ every way the count can pass the limit

    [Fact]
    public void A_new_physical_prim_over_the_limit_says_so_when_building_ends_and_not_before()
    {
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim torus = Add(scene, Torus(), Spot(0), true);
            var heard = new Heard(torus);
            Assert.Empty(heard.Details);          // the actor is made; core says the building is over next
            torus.Building = false;
            Say($"PRIM-COMPLEXITY new physical torus: {string.Join("|", heard.Details)}");
            Assert.Equal(new[] { "(12 of 10 convex hulls)" }, heard.Details);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_prim_made_physical_over_the_limit_says_so_at_once_unless_the_object_is_being_built()
    {
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim lone = Add(scene, Torus(), Spot(0), false);
            var heardLone = new Heard(lone);
            lone.IsPhysical = true;               // the build tool or llSetStatus on an object of one prim
            Assert.Equal(new[] { "(12 of 10 convex hulls)" }, heardLone.Details);

            JoltPrim building = Add(scene, Torus(), Spot(1), false);
            var heardBuilding = new Heard(building);
            building.Building = true;             // core marks the root first when it turns a linkset physical
            building.IsPhysical = true;
            Assert.Empty(heardBuilding.Details);
            building.Building = false;
            Assert.Equal(new[] { "(12 of 10 convex hulls)" }, heardBuilding.Details);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_linkset_built_part_by_part_is_counted_when_the_building_ends()
    {
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), false);
            var heard = new Heard(root);
            root.Building = true;
            root.IsPhysical = true;
            JoltPrim a = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 1.5f, 0f), false);
            JoltPrim b = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 3f, 0f), false);
            JoltPrim c = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 4.5f, 0f), false);
            foreach (JoltPrim p in new[] { a, b, c })
            {
                p.Building = true;
                p.IsPhysical = true;
                p.link(root);
            }
            Assert.Empty(heard.Details);          // 1 + 4 + 4 + 4 = 13: nothing said while it is being put together
            root.Building = false;
            Say($"PRIM-COMPLEXITY built linkset: {string.Join("|", heard.Details)}");
            Assert.Equal(new[] { "(13 of 10 convex hulls)" }, heard.Details);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Parts_arriving_one_by_one_in_a_physical_object_say_so_as_soon_as_the_count_is_over_and_growing()
    {
        // rez and region load: each part's actor is made and linked in turn, and the root is never marked as building
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            var heard = new Heard(root);
            JoltPrim a = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 1.5f, 0f), true);
            JoltPrim b = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 3f, 0f), true);
            JoltPrim c = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 4.5f, 0f), true);
            a.link(root);
            b.link(root);
            Assert.Empty(heard.Details);          // 1 + 4 + 4 = 9
            c.link(root);
            Say($"PRIM-COMPLEXITY parts arriving: {string.Join("|", heard.Details)}");
            Assert.Equal(new[] { "(13 of 10 convex hulls)" }, heard.Details);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_part_linked_into_a_physical_object_that_takes_it_past_the_limit_says_so()
    {
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            JoltPrim a = Add(scene, HollowBox(), Spot(0) + new Vector3(0f, 1.5f, 0f), true);
            a.link(root);
            root.Building = false;
            var heard = new Heard(root);
            JoltPrim ok = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0) + new Vector3(0f, 3f, 0f), true);
            ok.link(root);                        // 1 + 4 + 1 = 6
            Assert.Empty(heard.Details);
            JoltPrim torus = Add(scene, Torus(), Spot(0) + new Vector3(0f, 4.5f, 0f), true);
            torus.link(root);                     // 6 + 12 = 18
            Say($"PRIM-COMPLEXITY part linked in: {string.Join("|", heard.Details)}");
            Assert.Equal(new[] { "(18 of 10 convex hulls)" }, heard.Details);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_shape_or_shape_type_change_that_takes_a_physical_object_past_the_limit_says_so()
    {
        JoltScene scene = SceneWithLimit(10);
        try
        {
            JoltPrim lone = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            var shape = new Heard(lone);
            lone.Shape = Torus();                 // 1 -> 12
            Assert.Equal(new[] { "(12 of 10 convex hulls)" }, shape.Details);

            JoltPrim hollow = Add(scene, HollowBox(), Spot(1), true, Convex);
            var type = new Heard(hollow);
            hollow.PhysicsShapeType = Prim;       // 1 -> 4: under the limit
            Assert.Empty(type.Details);
            JoltPrim torus = Add(scene, Torus(), Spot(2), true, Convex);
            var typeOver = new Heard(torus);
            torus.PhysicsShapeType = Prim;        // 1 -> 12
            Assert.Equal(new[] { "(12 of 10 convex hulls)" }, typeOver.Details);
            Say($"PRIM-COMPLEXITY shape {string.Join("|", shape.Details)}; type {string.Join("|", typeOver.Details)}");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ what is not refused

    [Fact]
    public void An_object_at_the_limit_a_non_physical_one_and_every_object_with_no_limit_say_nothing()
    {
        JoltScene limited = SceneWithLimit(12);
        JoltScene unlimited = SceneWithLimit(0);
        try
        {
            JoltPrim atLimit = Add(limited, Torus(), Spot(0), false);
            var heardAt = new Heard(atLimit);
            atLimit.IsPhysical = true;            // 12 of 12
            atLimit.Building = false;
            Assert.Empty(heardAt.Details);

            JoltPrim idle = Add(limited, HollowBox(), Spot(1), false);
            var heardIdle = new Heard(idle);
            idle.Building = false;
            idle.Shape = Torus();                 // not physical: no limit applies to it
            Assert.Empty(heardIdle.Details);

            JoltPrim free = Add(unlimited, Torus(), Spot(0), true);
            var heardFree = new Heard(free);
            free.Building = false;
            free.Shape = Torus();
            Assert.Empty(heardFree.Details);
        }
        finally { limited.Dispose(); unlimited.Dispose(); }
    }

    // ------------------------------------------------------------------ the physics cost

    [Fact]
    public void The_physics_cost_an_actor_reports_is_its_count_and_is_not_scaled_by_size()
    {
        JoltScene scene = SceneWithLimit(0);
        try
        {
            JoltPrim box = Add(scene, PrimitiveBaseShape.CreateBox(), Spot(0), true);
            JoltPrim hollow = Add(scene, HollowBox(), Spot(1), true);
            JoltPrim torus = Add(scene, Torus(), Spot(2), true);
            JoltPrim none = Add(scene, HollowBox(), Spot(3), true);
            none.PhysicsShapeType = None;
            Say($"PRIM-COMPLEXITY cost: box {box.PhysicsCost}, hollow box {hollow.PhysicsCost}, torus {torus.PhysicsCost}, NONE {none.PhysicsCost}");
            Assert.Equal(new[] { 1f, 4f, 12f, 0f }, new[] { box.PhysicsCost, hollow.PhysicsCost, torus.PhysicsCost, none.PhysicsCost });
            Assert.All(new[] { box, hollow, torus, none }, p => Assert.True(p.PhysicsCostIsShapeCount));
        }
        finally { scene.Dispose(); }
    }
}
