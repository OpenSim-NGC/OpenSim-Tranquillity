/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using Rig = OpenSim.Region.PhysicsModules.Jolt.Tests.StepRateRig;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A mouse drag of a physical object as InWorldz Halcyon treats it (PhysxPrim.pid.cs DoGrabTarget, DoHoverTarget;
/// VehicleDynamics.cs Simulate): the drag's target moves the object like a move target and takes 90% of its spin away in each
/// of Halcyon's 15 ms steps (the same share per second at every step length here); a vehicle is not run by its controller
/// while it is dragged, and a drag of one is allowed (<see cref="JoltPrim.AllowsVehicleGrab"/>, <c>[Jolt] VehicleBehaviour =
/// InWorldz</c>). Under SecondLife the actor is told of a drag and does nothing with it. The actor is driven as core does in
/// SceneObjectGroup.SteerGrab: <see cref="JoltPrim.Grabbed"/>, then the move target, tau 0.25. Each result is a line
/// "Grab ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HalcyonGrabTests
{
    private readonly ITestOutputHelper _out;
    public HalcyonGrabTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private static Rig Make(double rate, int n, string basic = "InWorldz", string vehicle = "InWorldz")
        => new Rig(rate, n, jolt =>
        {
            jolt.Set("PrimLinearDamping", "0");
            jolt.Set("PrimAngularDamping", "0");
            jolt.Set("BasicForcesBehaviour", basic);
            jolt.Set("VehicleBehaviour", vehicle);
        });

    private static void Drag(JoltPrim prim, Vector3 target)
    {
        prim.Grabbed = true;       // as SteerGrab: before the target is switched on
        prim.PIDTau = 0.25f;
        prim.PIDTarget = target;
        prim.PIDActive = true;
    }

    private static void Release(JoltPrim prim)
    {
        prim.PIDActive = false;    // as EndGrab: the target off, then the drag over
        prim.Grabbed = false;
    }

    private static void Drive(Rig rig, Vector3 direction)
    {
        rig.Vector(Vehicle.LINEAR_MOTOR_DIRECTION, direction);
        rig.Float(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        rig.Float(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
    }

    // ------------------------------------------------------------------ the spin

    [Fact]
    public void A_drag_keeps_a_tenth_of_the_spin_a_15_ms_step_and_the_share_is_the_same_whatever_the_step()
    {
        const float h0 = HalcyonBasicForces.StepSeconds;
        Assert.Equal(0.1f, HalcyonBasicForces.GrabAngularKept(h0), 5);
        foreach (float h in new[] { 0.004f, 0.01f, 1f / 64f, 1f / 45f, 1f / 32f })
        {
            int parts = 7;
            float composed = MathF.Pow(HalcyonBasicForces.GrabAngularKept(h / parts), parts);
            Assert.Equal(HalcyonBasicForces.GrabAngularKept(h), composed, 4);
        }
        Assert.Equal(MathF.Pow(0.1f, 2f), HalcyonBasicForces.GrabAngularKept(2f * h0), 5);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_dragged_object_loses_its_spin_under_InWorldz_and_a_free_one_does_not(double rate, int n)
    {
        using Rig rig = Make(rate, n);
        JoltPrim free = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        free.RotationalVelocity = new Vector3(0f, 0f, 6f);
        rig.Scene.Simulate(Rig.Heartbeat);
        double freeSpin = Math.Abs(free.RotationalVelocity.Z);

        using Rig dragRig = Make(rate, n);
        JoltPrim dragged = dragRig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        dragged.RotationalVelocity = new Vector3(0f, 0f, 6f);
        Drag(dragged, new Vector3(100f, 150f, 100f));
        dragRig.Scene.Simulate(Rig.Heartbeat);
        double draggedSpin = Math.Abs(dragged.RotationalVelocity.Z);

        Say($"Grab spin after one heartbeat | {rate} Hz x {n} | free {freeSpin:0.0000} | dragged {draggedSpin:0.000000} (from 6)");
        Assert.True(freeSpin > 5.5, $"a free object kept {freeSpin}");
        Assert.True(draggedSpin < 0.001, $"a dragged object kept {draggedSpin}: Halcyon leaves 0.1^(0.0909 / 0.015) = 8e-7 of it");
    }

    [Fact]
    public void Under_SecondLife_a_drag_does_not_take_the_spin_away()
    {
        using Rig rig = Make(45, 2, basic: "SecondLife");
        JoltPrim dragged = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        dragged.RotationalVelocity = new Vector3(0f, 0f, 6f);
        Drag(dragged, new Vector3(100f, 150f, 100f));
        rig.Scene.Simulate(Rig.Heartbeat);
        double spin = Math.Abs(dragged.RotationalVelocity.Z);
        Say($"Grab SecondLife spin after one heartbeat of a drag: {spin:0.0000} (from 6)");
        Assert.True(spin > 5.5, $"the spin fell to {spin}");
    }

    [Fact]
    public void A_drag_moves_the_object_to_the_target_like_a_move_target_and_lets_go_when_it_ends()
    {
        using Rig rig = Make(45, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        Drag(box, new Vector3(100f, 138f, 100f));
        rig.RunTo(6.0);
        Say($"Grab dragged 10 m: at y {box.Position.Y:0.000} after 6 s");
        Assert.InRange(box.Position.Y, 137.9f, 138.1f);
        Release(box);
        rig.RunTo(7.0);
        Assert.InRange(box.Position.Y, 137.8f, 138.2f);   // weightless (buoyancy 1) and at rest where it was let go
    }

    [Fact]
    public void A_drag_overrides_a_rotation_target_and_it_acts_again_when_the_drag_ends_under_InWorldz()
    {
        using Rig rig = Make(45, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        box.APIDStrength = 1f;
        box.APIDDamping = 1f;
        box.APIDActive = true;
        Drag(box, new Vector3(100f, 128f, 100f));         // the target is where it is: only the override is tested
        rig.RunTo(2.0);
        double turnedDragged = TurnDegrees(box);
        Release(box);
        rig.RunTo(6.0);
        double turnedAfter = TurnDegrees(box);
        Say($"Grab look-at: turned {turnedDragged:0.00} degrees during the drag, {turnedAfter:0.0} after it");
        Assert.True(turnedDragged < 1.0, $"the look-at turned a dragged object {turnedDragged} degrees");
        Assert.InRange(turnedAfter, 88.0, 92.0);
    }

    [Fact]
    public void Under_SecondLife_a_drag_does_not_override_a_rotation_target()
    {
        using Rig rig = Make(45, 2, basic: "SecondLife");
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        box.APIDStrength = 1f;
        box.APIDDamping = 1f;
        box.APIDActive = true;
        Drag(box, new Vector3(100f, 128f, 100f));
        rig.RunTo(6.0);
        double turned = TurnDegrees(box);
        Say($"Grab SecondLife look-at during a drag: turned {turned:0.0} degrees");
        Assert.InRange(turned, 85.0, 95.0);
    }

    private static double TurnDegrees(JoltPrim box)
    {
        Quaternion q = box.Orientation;
        return 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(q.W))) * 180.0 / Math.PI;
    }

    // ------------------------------------------------------------------ a vehicle

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_dragged_vehicle_is_run_by_the_drag_and_not_by_its_controller_under_InWorldz(double rate, int n)
    {
        using Rig rig = Make(rate, n);
        JoltPrim car = rig.AddBox(new Vector3(3f, 1.5f, 1f), new Vector3(100f, 128f, 100f), vehicle: true);
        Drive(rig, new Vector3(10f, 0f, 0f));
        rig.RunTo(6.0);
        double driven = car.Velocity.X;

        Drag(car, new Vector3(car.Position.X, car.Position.Y + 30f, car.Position.Z));
        rig.RunTo(9.0);
        Say($"Grab vehicle | {rate} Hz x {n} | driven {driven:0.00} m/s in x, dragged: vx {car.Velocity.X:0.000} vy {car.Velocity.Y:0.000} at y {car.Position.Y:0.00}");
        Assert.True(driven > 8.0, $"the car was driven at {driven}");
        Assert.True(Math.Abs(car.Velocity.X) < 0.05, $"the controller still drove the car: vx {car.Velocity.X}");
        Assert.True(car.Position.Y > 128f + 20f, $"the drag did not take the car: y {car.Position.Y}");
    }

    [Fact]
    public void Under_SecondLife_a_vehicle_keeps_its_controller_and_the_drag_is_not_allowed()
    {
        using Rig rig = Make(45, 2, vehicle: "SecondLife");
        JoltPrim car = rig.AddBox(new Vector3(3f, 1.5f, 1f), new Vector3(100f, 128f, 100f), vehicle: true);
        Assert.False(car.AllowsVehicleGrab);
        Drive(rig, new Vector3(10f, 0f, 0f));
        rig.RunTo(6.0);
        Drag(car, new Vector3(car.Position.X, car.Position.Y + 30f, car.Position.Z));   // not reachable through core, which refuses it
        rig.RunTo(9.0);
        Say($"Grab SecondLife vehicle: vx {car.Velocity.X:0.00}, vy {car.Velocity.Y:0.000}");
        Assert.True(car.Velocity.X > 8.0, $"the controller stopped: vx {car.Velocity.X}");
        Assert.True(Math.Abs(car.Velocity.Y) < 0.05, $"the drag moved the vehicle: vy {car.Velocity.Y}");
    }

    [Fact]
    public void The_behaviour_decides_what_the_engine_tells_core_about_a_vehicle_drag_and_a_type_change()
    {
        using (Rig iw = Make(45, 2))
        {
            JoltPrim p = iw.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
            Assert.True(p.AllowsVehicleGrab);
            Assert.True(p.VehicleTypeStopsTargets);
        }
        using (Rig sl = Make(45, 2, vehicle: "SecondLife"))
        {
            JoltPrim p = sl.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
            Assert.False(p.AllowsVehicleGrab);
            Assert.False(p.VehicleTypeStopsTargets);
        }
    }

    [Fact]
    public void After_a_long_drag_the_vehicle_has_its_gravity_rules_back_and_its_motors_reset_as_Halcyons_do()
    {
        using Rig rig = Make(45, 2);
        JoltPrim car = rig.AddBox(new Vector3(3f, 1.5f, 1f), new Vector3(100f, 128f, 100f), vehicle: true);
        Drive(rig, new Vector3(10f, 0f, 0f));
        rig.RunTo(1.0);
        Drag(car, new Vector3(100f, 140f, 100f));
        rig.RunTo(5.0);                                   // more than a second: the controller's clock stood still
        Release(car);
        rig.RunTo(6.0);
        double z = car.Position.Z;
        double vx = car.Velocity.X;
        Say($"Grab after a 4 s drag: vx {vx:0.000}, z {z:0.000} (the car floats at buoyancy 1)");
        Assert.True(Math.Abs(z - 100.0) < 0.5, $"the car fell or rose to z {z}");
        Assert.True(Math.Abs(vx) < 1.0, $"the motors were not reset: vx {vx}");   // CheckResetMotors: more than 1 s since the last step
        Drive(rig, new Vector3(10f, 0f, 0f));              // a script sets them again
        rig.RunTo(12.0);
        Assert.True(car.Velocity.X > 8.0, $"the controller does not drive the car again: vx {car.Velocity.X}");
    }
}
