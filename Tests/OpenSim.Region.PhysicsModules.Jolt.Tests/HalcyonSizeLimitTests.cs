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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Under <c>[Jolt] BasicForcesBehaviour = InWorldz</c> a physical object outside Halcyon's size limits is made kinematic and
/// nobody is told (PhysxPrim.stability.cs EnforceMinimumDynamicDimensions and EnforceMaximumDynamicDimensions: the actor is set
/// kinematic, no message is sent, and the physical flag stays set). The test takes Halcyon's own decision from the translated
/// model (HalcyonSizeChecks) and checks the module does what Halcyon does with it: no notice, the physical flag still set, the
/// body held where it is while a body inside the limits falls. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class HalcyonSizeLimitTests
{
    private readonly ITestOutputHelper _out;
    public HalcyonSizeLimitTests(ITestOutputHelper output) => _out = output;

    private static uint _id = 47000;
    private const float Heartbeat = 1f / 11f;

    private static JoltPrim Add(JoltScene scene, Vector3 size, Vector3 at)
        => (JoltPrim)scene.AddPrimShape("prim", PrimitiveBaseShape.CreateBox(), at, size, Quaternion.Identity, true, false,
                                        (byte)PhysShapeType.prim, _id++);

    [Theory]
    [InlineData(0.005f, 0.005f, 1f)]    // two sides under 0.009 m
    [InlineData(300f, 0.5f, 0.5f)]      // one side over 256 m
    [InlineData(200f, 200f, 1f)]        // two sides over 128 m
    public void An_object_Halcyon_makes_kinematic_is_held_silently_with_its_physical_flag_set(float x, float y, float z)
    {
        Assert.True(HalcyonSizeChecks.MakesKinematic(x, y, z), "the scenario is one Halcyon refuses");
        JoltScene scene = NewScene(Scratch(), ("BasicForcesBehaviour", "InWorldz"));
        try
        {
            JoltPrim refused = Add(scene, new Vector3(x, y, z), new Vector3(CentreX, CentreY, Ground + 40f));
            JoltPrim control = Add(scene, new Vector3(1f, 1f, 1f), new Vector3(CentreX + 5f, CentreY, Ground + 40f));
            refused.Building = false;
            control.Building = false;
            Vector3 start = refused.Position, controlStart = control.Position;
            for (int i = 0; i < 22; i++)
                scene.Simulate(Heartbeat);
            double held = (refused.Position - start).Length(), fell = controlStart.Z - control.Position.Z;
            _out.WriteLine($"size {x} x {y} x {z}: flag set {refused.IsPhysical}, moved {held:0.000} m; the control fell {fell:0.000} m");
            Assert.True(refused.IsPhysical, "Halcyon leaves the physical flag set");
            Assert.True(fell > 5.0, $"the control did not fall: {fell}");
            Assert.True(held < 0.02, $"the refused object moved {held} m");
        }
        finally { scene.Dispose(); }
    }
}
