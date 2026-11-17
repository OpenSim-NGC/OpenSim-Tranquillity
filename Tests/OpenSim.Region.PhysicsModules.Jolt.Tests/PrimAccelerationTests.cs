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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical prim's Acceleration (what llGetAccel reads, through SceneObjectPart.Acceleration) is its change of velocity over
/// the last step, as ubODE works it out (ODEPrim.cs, the velocity update after each step): about -9.8 m/s^2 in z while it
/// falls freely, zero once it rests, and zero for a non-physical prim. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimAccelerationTests
{
    private readonly ITestOutputHelper _out;
    public PrimAccelerationTests(ITestOutputHelper output) { _out = output; }

    private static uint _id = 53000;

    [Fact]
    public void A_falling_prim_accelerates_down_and_a_resting_one_does_not()
    {
        JoltScene scene = MeshShapesTodayTests.NewScene(MeshShapesTodayTests.Scratch());
        try
        {
            PhysicsActor box = scene.AddPrimShape("drop", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, 60f), new Vector3(0.5f, 0.5f, 0.5f),
                                                  Quaternion.Identity, true, false, (byte)PhysShapeType.prim, _id++);
            // Falling: a few heartbeats after the drop it is far from the ground (25 m) and moving.
            for (int i = 0; i < 6; i++)
                scene.Simulate(MeshShapesTodayTests.Heartbeat);
            Vector3 falling = box.Acceleration;
            _out.WriteLine($"ACCEL falling z={box.Position.Z:0.00} v={box.Velocity} a={falling}");
            Assert.True(box.Position.Z > 40f, $"the box is not falling freely: z {box.Position.Z}");
            // Gravity 9.8 less the 0.02 linear damping's small share: between 9.3 and 9.9 down, nothing sideways.
            Assert.InRange(falling.Z, -9.9f, -9.3f);
            Assert.True(MathF.Abs(falling.X) < 0.1f && MathF.Abs(falling.Y) < 0.1f, $"sideways acceleration {falling}");

            // Resting: let it land and settle; once it rests its acceleration is zero (poll, with a generous cap).
            Vector3 resting = falling;
            for (int i = 0; i < 400 && resting != Vector3.Zero; i++)
            {
                scene.Simulate(MeshShapesTodayTests.Heartbeat);
                resting = box.Acceleration;
            }
            _out.WriteLine($"ACCEL resting z={box.Position.Z:0.00} v={box.Velocity} a={resting}");
            Assert.True(box.Position.Z < 26f, $"the box did not land: z {box.Position.Z}");
            Assert.Equal(Vector3.Zero, resting);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_non_physical_prim_has_no_acceleration()
    {
        JoltScene scene = MeshShapesTodayTests.NewScene(MeshShapesTodayTests.Scratch());
        try
        {
            PhysicsActor box = scene.AddPrimShape("drop", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, 60f), new Vector3(0.5f, 0.5f, 0.5f),
                                                  Quaternion.Identity, true, false, (byte)PhysShapeType.prim, _id++);
            for (int i = 0; i < 6; i++)
                scene.Simulate(MeshShapesTodayTests.Heartbeat);
            Assert.NotEqual(Vector3.Zero, box.Acceleration);   // falling: it has one
            box.IsPhysical = false;
            scene.Simulate(MeshShapesTodayTests.Heartbeat);
            Assert.Equal(Vector3.Zero, box.Acceleration);
        }
        finally { scene.Dispose(); }
    }
}
