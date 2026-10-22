/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The InWorldz camera steering, under the InWorldz behaviour only (VehicleDynamics.cs:792-861; read, not run): outside the
/// dead zone the turn is the camera's offset shaped by (e^x - 1) / (e - 1) as a share of the script's motor direction; each
/// camera update restarts the motor's decay; pitch is not steered under LIMIT_ROLL_ONLY; a script's own angular motor is
/// ignored while the driver is in mouselook; MOUSEPOINT_STEER and MOUSEPOINT_BANK steer and bank in any view. Second Life's
/// behaviour, the default, has none of these. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleInWorldzMouselookRulesTests
{
    private const double Deg = Math.PI / 180.0;
    private const int SteerFlag = 0x80, BankFlag = 0x100, RollOnlyFlag = 0x2;
    private const int PointSteerFlag = 0x100000, PointBankFlag = 0x200000;       // a script's numbers

    private static BehaviourRig Rig(VehicleBehaviour behaviour, int flags, double yawDegrees, double pitchDegrees, Vector3 motor,
                                    bool inMouselook = true, bool keepRollOnly = false, float decay = 120f)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = behaviour }, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, decay);
        // The car preset has LIMIT_ROLL_ONLY, which keeps the camera's pitch out of it; most rows are about the pitch.
        if (!keepRollOnly)
            rig.Car.ProcessVehicleFlags(RollOnlyFlag, remove: true);
        rig.Car.ProcessVehicleFlags(flags, remove: false);
        double y = yawDegrees * Deg, p = pitchDegrees * Deg;
        var at = new Vector3((float)(Math.Cos(p) * Math.Cos(y)), (float)(Math.Cos(p) * Math.Sin(y)), (float)-Math.Sin(p));
        rig.Car.DriverCamera = () => new DriverView(at, inMouselook);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, motor);
        return rig;
    }

    // The share of the motor direction InWorldz turns at for a camera `degrees` off the heading on the default range of pi / 4:
    // sin(angle) * pi / 2 / (pi / 4) held to -1..1, nothing within pi / 32 of it, (e^x - 1) / (e - 1) beyond.
    private static double Shaped(double degrees)
    {
        double x = Math.Clamp(Math.Sin(degrees * Deg) * 2.0, -1.0, 1.0);
        double size = Math.Abs(x);
        return size <= Math.PI / 32.0 ? 0.0 : Math.Sign(x) * (Math.Exp(size) - 1.0) / (Math.E - 1.0);
    }

    private static void Same(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 1e-3 * Math.Abs(expected), $"{actual:R} against {expected:R}");

    // The soft exponential outside the zone: 6 percent at the edge, 39 percent at 15 degrees, whole from 30 degrees. The
    // motor direction InWorldz sets is that share of the script's, and the reference is an InWorldz vehicle (no flags) whose
    // motor direction is set to that share by the script: the same angular motor, so the same first step.
    [Theory]
    [InlineData(2.9)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(90.0)]
    [InlineData(-15.0)]
    public void Outside_the_zone_the_turn_is_the_shaped_share_of_the_motor_direction(double yawDegrees)
    {
        var iw = Rig(VehicleBehaviour.InWorldz, SteerFlag, yawDegrees, 0, new Vector3(0f, 0f, 2f));
        var reference = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz }, Vehicle.TYPE_CAR, gravity: false);
        reference.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        reference.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        reference.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, (float)(2.0 * Shaped(yawDegrees))));
        iw.Step(1.0 / 45);
        reference.Step(1.0 / 45);
        Assert.NotEqual(0f, iw.Body.AngularVelocity.Z);
        Same(reference.Body.AngularVelocity.Z, iw.Body.AngularVelocity.Z);
        // The motor direction itself (the first step's velocity is the motor's start from rest, whatever a small direction).
        Same(2.0 * Shaped(yawDegrees), iw.Car.AngularMotorDirectionInEffect.Z);
    }

    // Halcyon's turn is a rate that follows the camera's offset, not an aim at the camera: a camera 30 degrees or more off
    // turns at the whole motor direction, and a small offset at a small share of it.
    [Fact]
    public void The_turn_is_a_share_of_the_direction_that_saturates_at_thirty_degrees()
    {
        double rate(double deg)
        {
            var r = Rig(VehicleBehaviour.InWorldz, SteerFlag, deg, 0, new Vector3(0f, 0f, 2f));
            r.Step(1.0 / 45);
            return r.Car.AngularMotorDirectionInEffect.Z;      // the rate the motor is asked for
        }
        Assert.True(rate(5) < 0.5 * rate(15), "5 degrees turns slower than half of 15");
        Assert.True(rate(15) < 0.5 * rate(60), "15 degrees turns slower than half of the full rate");
        Same(rate(30), rate(90));
    }

    // Every camera update sets the motor again, so its decay clock restarts: with the camera held off the heading the motor
    // does not fade, where Second Life's motor (set once by the script) fades from that set on.
    [Fact]
    public void Each_camera_update_restarts_the_motors_decay()
    {
        var iw = Rig(VehicleBehaviour.InWorldz, SteerFlag, 30, 0, new Vector3(0f, 0f, 2f), decay: 1f);
        var sl = Rig(VehicleBehaviour.SecondLife, SteerFlag, 30, 0, new Vector3(0f, 0f, 2f), decay: 1f);
        for (int i = 0; i < 90; i++)
        {
            iw.Car.Step(1f / 45);            // the car itself is left where it is, so the camera stays 30 degrees off
            sl.Car.Step(1f / 45);
        }
        Assert.True(iw.Car.AngularMotorAge < 0.1f, $"InWorldz's motor is {iw.Car.AngularMotorAge} s old");
        Assert.True(sl.Car.AngularMotorAge > 1.5f, $"Second Life's motor is {sl.Car.AngularMotorAge} s old");
    }

    // With LIMIT_ROLL_ONLY the pitch is not steered under InWorldz (the yaw still is); Second Life's pitch aim does not look at it.
    [Fact]
    public void Pitch_is_not_steered_under_LIMIT_ROLL_ONLY()
    {
        var motor = new Vector3(0f, 2f, 2f);
        var iwRollOnly = Rig(VehicleBehaviour.InWorldz, SteerFlag, 10, 10, motor, keepRollOnly: true);
        var iwFree = Rig(VehicleBehaviour.InWorldz, SteerFlag, 10, 10, motor);
        var slRollOnly = Rig(VehicleBehaviour.SecondLife, SteerFlag, 10, 10, motor, keepRollOnly: true);
        foreach (var r in new[] { iwRollOnly, iwFree, slRollOnly })
            r.Step(1.0 / 45);
        Assert.Equal(0f, iwRollOnly.Body.AngularVelocity.Y);
        Assert.NotEqual(0f, iwRollOnly.Body.AngularVelocity.Z);
        Assert.NotEqual(0f, iwFree.Body.AngularVelocity.Y);
        Assert.NotEqual(0f, slRollOnly.Body.AngularVelocity.Y);
    }

    // The script's own angular motor is stored but does not move the motor while the driver is in mouselook with a mouselook
    // flag set; out of mouselook it does.
    [Fact]
    public void A_scripts_own_motor_is_ignored_while_the_driver_is_in_mouselook()
    {
        var inML = Rig(VehicleBehaviour.InWorldz, SteerFlag, 0, 0, new Vector3(0f, 0f, 2f));
        var outML = Rig(VehicleBehaviour.InWorldz, SteerFlag, 0, 0, new Vector3(0f, 0f, 2f), inMouselook: false);
        Assert.Equal(Vector3.Zero, inML.Car.AngularMotorDirectionInEffect);
        Assert.Equal(new Vector3(0f, 0f, 2f), outML.Car.AngularMotorDirectionInEffect);
        // The direction is kept as what the camera's share is taken of.
        Assert.Equal(new Vector3(0f, 0f, 2f), inML.Car.GetVecParam(VehVectorParam.AngularMotorDirection));
        inML.Run(1.0 / 45, 45);
        Assert.Equal(0f, inML.Body.AngularVelocity.Z);
    }

    [Fact]
    public void Second_Life_does_not_ignore_a_scripts_motor_in_mouselook()
    {
        var sl = Rig(VehicleBehaviour.SecondLife, SteerFlag, 0, 0, new Vector3(0f, 0f, 2f));
        Assert.Equal(new Vector3(0f, 0f, 2f), sl.Car.AngularMotorDirectionInEffect);
    }

    // MOUSEPOINT_STEER and MOUSEPOINT_BANK act in any view under InWorldz; Second Life's behaviour holds them and does nothing.
    [Fact]
    public void MOUSEPOINT_STEER_turns_the_vehicle_toward_the_camera_in_any_view()
    {
        // The script's own motor turns a vehicle at 2 rad/s's pull whatever the flag; InWorldz sets it to the camera's share.
        var none = Rig(VehicleBehaviour.SecondLife, 0, 20, 0, new Vector3(0f, 0f, 2f), inMouselook: false);
        var iw = Rig(VehicleBehaviour.InWorldz, PointSteerFlag, 20, 0, new Vector3(0f, 0f, 2f), inMouselook: false);
        var sl = Rig(VehicleBehaviour.SecondLife, PointSteerFlag, 20, 0, new Vector3(0f, 0f, 2f), inMouselook: false);
        iw.Step(1.0 / 45);
        sl.Step(1.0 / 45);
        none.Step(1.0 / 45);
        Assert.Equal(0f, iw.Body.AngularVelocity.X);
        Assert.Equal(none.Body.AngularVelocity.Z, sl.Body.AngularVelocity.Z);            // Second Life: the flag does nothing
        Assert.True(iw.Body.AngularVelocity.Z > 0f);
        Same(2.0 * Shaped(20), iw.Car.AngularMotorDirectionInEffect.Z);                  // InWorldz: the shaped share of the script's
    }

    [Fact]
    public void MOUSEPOINT_BANK_rolls_the_vehicle_by_the_cameras_yaw_in_any_view()
    {
        var none = Rig(VehicleBehaviour.SecondLife, 0, 20, 0, new Vector3(2f, 0f, 0f), inMouselook: false);
        var iw = Rig(VehicleBehaviour.InWorldz, PointBankFlag, 20, 0, new Vector3(2f, 0f, 0f), inMouselook: false);
        var sl = Rig(VehicleBehaviour.SecondLife, PointBankFlag, 20, 0, new Vector3(2f, 0f, 0f), inMouselook: false);
        iw.Step(1.0 / 45);
        sl.Step(1.0 / 45);
        none.Step(1.0 / 45);
        Assert.Equal(none.Body.AngularVelocity.X, sl.Body.AngularVelocity.X);            // Second Life: the flag does nothing
        Assert.True(iw.Body.AngularVelocity.X < 0f);
        Same(-2.0 * Shaped(20), iw.Car.AngularMotorDirectionInEffect.X);                  // InWorldz: rolls by the camera's yaw (the share, negated)
    }

    // The mouselook flags, out of mouselook, do nothing under InWorldz (the camera is not the driver's aim there).
    [Fact]
    public void The_mouselook_flags_still_need_mouselook()
    {
        var iw = Rig(VehicleBehaviour.InWorldz, SteerFlag, 20, 0, new Vector3(0f, 0f, 2f), inMouselook: false);
        iw.Run(1.0 / 45, 45);
        // (The script's own motor acts there: its z is 2 and nothing steers it.)
        Assert.Equal(new Vector3(0f, 0f, 2f), iw.Car.AngularMotorDirectionInEffect);
    }

    // Steering sets (0, pitch, yaw); banking only (-yaw, 0, 0), and the pitch and yaw of the script's direction do not reach it.
    [Fact]
    public void Steering_sets_pitch_and_yaw_and_banking_only_the_roll()
    {
        var motor = new Vector3(2f, 2f, 2f);
        var steer = Rig(VehicleBehaviour.InWorldz, SteerFlag, 10, 10, motor);
        var bank = Rig(VehicleBehaviour.InWorldz, BankFlag, 10, 10, motor);
        var both = Rig(VehicleBehaviour.InWorldz, SteerFlag | BankFlag, 10, 10, motor);
        foreach (var r in new[] { steer, bank, both })
            r.Step(1.0 / 45);
        Assert.Equal(0f, steer.Body.AngularVelocity.X);
        Assert.NotEqual(0f, steer.Body.AngularVelocity.Y);
        Assert.NotEqual(0f, steer.Body.AngularVelocity.Z);
        Assert.NotEqual(0f, bank.Body.AngularVelocity.X);
        Assert.Equal(0f, bank.Body.AngularVelocity.Y);
        Assert.Equal(0f, bank.Body.AngularVelocity.Z);
        Assert.Equal(bank.Body.AngularVelocity, both.Body.AngularVelocity);        // banking is the later set and stands
    }

    // A vehicle with a MOUSEPOINT flag is never let to sleep under InWorldz, as with the mouselook flags; the flags do
    // nothing under Second Life, so it may.
    [Fact]
    public void A_vehicle_with_a_MOUSEPOINT_flag_is_not_let_to_sleep_under_InWorldz()
    {
        foreach (int flag in new[] { PointSteerFlag, PointBankFlag })
        {
            var iw = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz }, Vehicle.TYPE_CAR, gravity: false);
            var sl = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife }, Vehicle.TYPE_CAR, gravity: false);
            Assert.True(iw.Car.IsIdle);
            Assert.True(sl.Car.IsIdle);
            iw.Car.ProcessVehicleFlags(flag, remove: false);
            sl.Car.ProcessVehicleFlags(flag, remove: false);
            Assert.False(iw.Car.IsIdle);
            Assert.True(sl.Car.IsIdle);
        }
    }
}
