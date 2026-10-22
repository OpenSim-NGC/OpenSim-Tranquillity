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
/// The sizes a physical object may have under <c>[Jolt] BasicForcesBehaviour = InWorldz</c>, as Halcyon allows them
/// (PhysxPrim.stability.cs EnforceMinimumDynamicDimensions and EnforceMaximumDynamicDimensions): an object whose box, lined up
/// with the region's axes and holding every part of the linkset, has two or more sides under 0.009 m, a side over 256 m, or two
/// sides over 128 m, is not simulated as physical: Halcyon makes the actor kinematic, tells nobody and leaves the physical flag
/// set. Here the root's actor is held where it is (<see cref="JoltPrim.IsHeldBySizeLimit"/>) and the owner is not told, wherever
/// Halcyon rebuilds the actor (made physical, resized, a part linked in, a shape changed). Under
/// SecondLife nothing is held. Each result is a line "PRIM-SIZE ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicalSizeLimitTests
{
    private readonly ITestOutputHelper _out;
    public PhysicalSizeLimitTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static uint _id = 46000;

    private static JoltPrim Add(JoltScene scene, Vector3 size, Vector3 at, bool physical, Quaternion? rotation = null)
        => (JoltPrim)scene.AddPrimShape("prim", PrimitiveBaseShape.CreateBox(), at, size, rotation ?? Quaternion.Identity, physical, false,
                                        (byte)PhysShapeType.prim, _id++);

    private static JoltScene SceneUnder(string behaviour) => NewScene(Scratch(), ("BasicForcesBehaviour", behaviour));

    // ------------------------------------------------------------------ the rule itself (Halcyon's two functions)

    [Theory]
    [InlineData(1f, 1f, 1f, HalcyonPhysicalLimits.Verdict.Ok)]
    [InlineData(0.5f, 0.008f, 3f, HalcyonPhysicalLimits.Verdict.Ok)]          // one thin side is allowed
    [InlineData(0.5f, 0.008f, 0.008f, HalcyonPhysicalLimits.Verdict.TooSmall)]  // two are not
    [InlineData(0.009f, 0.009f, 5f, HalcyonPhysicalLimits.Verdict.Ok)]        // 0.009 itself is not under 0.009
    [InlineData(0.00899f, 0.00899f, 5f, HalcyonPhysicalLimits.Verdict.TooSmall)]
    [InlineData(0.001f, 0.001f, 0.001f, HalcyonPhysicalLimits.Verdict.TooSmall)]
    [InlineData(256f, 1f, 1f, HalcyonPhysicalLimits.Verdict.Ok)]              // 256 itself is not over 256
    [InlineData(256.01f, 1f, 1f, HalcyonPhysicalLimits.Verdict.TooLarge)]
    [InlineData(1f, 1f, 300f, HalcyonPhysicalLimits.Verdict.TooLarge)]
    [InlineData(200f, 1f, 1f, HalcyonPhysicalLimits.Verdict.Ok)]              // one long side is allowed
    [InlineData(128f, 128f, 128f, HalcyonPhysicalLimits.Verdict.Ok)]          // 128 itself is not over 128
    [InlineData(128.01f, 128.01f, 1f, HalcyonPhysicalLimits.Verdict.TooLarge)]
    [InlineData(1f, 200f, 200f, HalcyonPhysicalLimits.Verdict.TooLarge)]
    [InlineData(0.005f, 0.005f, 300f, HalcyonPhysicalLimits.Verdict.TooSmall)]  // too small is checked first
    public void Halcyons_two_checks_on_the_box(float x, float y, float z, HalcyonPhysicalLimits.Verdict expected)
    {
        Assert.Equal(expected, HalcyonPhysicalLimits.Check(x, y, z));
    }

    // ------------------------------------------------------------------ in a scene

    [Fact]
    public void A_new_physical_prim_too_long_or_too_flat_says_so_when_building_ends()
    {
        JoltScene scene = SceneUnder("InWorldz");
        try
        {
            JoltPrim ok = Add(scene, new Vector3(2f, 1f, 1f), new Vector3(CentreX, CentreY, Ground + 3f), true);
            JoltPrim flat = Add(scene, new Vector3(0.005f, 0.005f, 1f), new Vector3(CentreX + 4f, CentreY, Ground + 3f), true);
            JoltPrim rod = Add(scene, new Vector3(300f, 0.5f, 0.5f), new Vector3(CentreX, CentreY + 4f, Ground + 3f), true);
            Assert.False(flat.IsHeldBySizeLimit);  // the actor is made; core says the building is over next
            ok.Building = false;
            flat.Building = false;
            rod.Building = false;
            Say($"PRIM-SIZE new physical prims: ok held {ok.IsHeldBySizeLimit}, flat held {flat.IsHeldBySizeLimit}, rod held {rod.IsHeldBySizeLimit}");
            Assert.False(ok.IsHeldBySizeLimit);
            Assert.True(flat.IsHeldBySizeLimit);
            Assert.True(rod.IsHeldBySizeLimit);
            Assert.True(flat.IsPhysical && rod.IsPhysical, "Halcyon leaves the physical flag set");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Under_SecondLife_the_same_prims_say_nothing()
    {
        JoltScene scene = SceneUnder("SecondLife");
        try
        {
            JoltPrim flat = Add(scene, new Vector3(0.005f, 0.005f, 1f), new Vector3(CentreX + 4f, CentreY, Ground + 3f), true);
            JoltPrim rod = Add(scene, new Vector3(300f, 0.5f, 0.5f), new Vector3(CentreX, CentreY + 4f, Ground + 3f), true);
            flat.Building = false;
            rod.Building = false;
            flat.Size = new Vector3(0.004f, 0.004f, 1f);
            rod.Size = new Vector3(400f, 0.5f, 0.5f);
            Say($"PRIM-SIZE under SecondLife: flat held {flat.IsHeldBySizeLimit}, rod held {rod.IsHeldBySizeLimit}");
            Assert.False(flat.IsHeldBySizeLimit);
            Assert.False(rod.IsHeldBySizeLimit);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_physical_prim_resized_out_of_the_limits_is_held_and_one_resized_back_in_is_let_go()
    {
        JoltScene scene = SceneUnder("InWorldz");
        try
        {
            JoltPrim p = Add(scene, new Vector3(2f, 1f, 1f), new Vector3(CentreX, CentreY, Ground + 3f), true);
            p.Building = false;
            p.Size = new Vector3(2f, 1.5f, 1f);
            Assert.False(p.IsHeldBySizeLimit);
            p.Size = new Vector3(0.002f, 0.002f, 1f);
            Assert.True(p.IsHeldBySizeLimit);
            p.Size = new Vector3(2f, 1f, 1f);
            Assert.False(p.IsHeldBySizeLimit);     // back inside the limits: let go
            p.Size = new Vector3(260f, 1f, 1f);
            Say($"PRIM-SIZE resized: held {p.IsHeldBySizeLimit}");
            Assert.True(p.IsHeldBySizeLimit);
            Assert.True(p.IsPhysical);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_prim_made_physical_that_is_too_large_is_held_at_once_and_a_non_physical_one_is_never_checked()
    {
        JoltScene scene = SceneUnder("InWorldz");
        try
        {
            JoltPrim idle = Add(scene, new Vector3(300f, 300f, 1f), new Vector3(CentreX, CentreY, Ground + 3f), false);
            idle.Building = false;
            idle.Size = new Vector3(400f, 400f, 1f);
            Assert.False(idle.IsHeldBySizeLimit);  // static: no limit (core's own scene limits apply)
            idle.IsPhysical = true;
            Assert.True(idle.IsHeldBySizeLimit);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_object_that_is_too_complex_and_too_large_is_reported_for_its_complexity_only()
    {
        // Halcyon checks the complexity before it builds the actor and the sizes after it, so the first reason is the one given.
        JoltScene scene = NewScene(Scratch(), ("BasicForcesBehaviour", "InWorldz"), ("MaxPhysicalComplexity", "2"));
        try
        {
            PrimitiveBaseShape hollow = Shapes().Single(s => s.Name == "hollow box 0.5").Build();   // 4 hulls
            var prim = (JoltPrim)scene.AddPrimShape("prim", hollow, new Vector3(CentreX, CentreY, Ground + 3f), new Vector3(300f, 1f, 1f),
                                                    Quaternion.Identity, true, false, (byte)PhysShapeType.prim, _id++);
            var complexity = new List<string>();
            prim.OnComplexityError += d => complexity.Add(d);
            prim.Building = false;
            Say($"PRIM-SIZE too complex and too large: complexity [{string.Join("|", complexity)}]");
            Assert.Equal(new[] { "(4 of 2 convex hulls)" }, complexity);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_box_holds_every_part_and_is_lined_up_with_the_region_so_a_turned_rod_can_be_too_large_where_a_straight_one_is_not()
    {
        JoltScene scene = SceneUnder("InWorldz");
        try
        {
            // a straight rod 200 m long is one long side: allowed
            JoltPrim straight = Add(scene, new Vector3(200f, 1f, 1f), new Vector3(CentreX, CentreY, Ground + 3f), true);
            straight.Building = false;
            Assert.False(straight.IsHeldBySizeLimit);

            // turned 45 degrees about z, the same rod spans about 141 m in x and in y: two sides over 128 m
            Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f);
            JoltPrim diagonal = Add(scene, new Vector3(200f, 1f, 1f), new Vector3(CentreX, CentreY, Ground + 6f), true, turned);
            diagonal.Building = false;
            Assert.True(diagonal.IsHeldBySizeLimit);

            // two 100 m prims linked end to end make a 200 m side: one long side, allowed; parts across make a second one
            JoltPrim root = Add(scene, new Vector3(100f, 1f, 1f), new Vector3(CentreX, CentreY, Ground + 9f), true);
            root.Building = false;
            JoltPrim end = Add(scene, new Vector3(100f, 1f, 1f), new Vector3(CentreX + 100f, CentreY, Ground + 9f), true);
            end.link(root);
            Assert.False(root.IsHeldBySizeLimit);
            JoltPrim across = Add(scene, new Vector3(1f, 100f, 1f), new Vector3(CentreX + 50f, CentreY + 50f, Ground + 9f), true);
            across.link(root);   // x about 200 m, y about 100 m: only one side over 128 m
            Assert.False(root.IsHeldBySizeLimit);
            JoltPrim farAcross = Add(scene, new Vector3(1f, 100f, 1f), new Vector3(CentreX + 50f, CentreY + 130f, Ground + 9f), true);
            farAcross.link(root);   // y now spans about 180 m
            Say($"PRIM-SIZE linkset: held {root.IsHeldBySizeLimit}");
            Assert.True(root.IsHeldBySizeLimit);
        }
        finally { scene.Dispose(); }
    }
}
