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
/// Four small InWorldz rules, under the InWorldz behaviour only (VehicleDynamics.cs, read, not run): an upward motor change
/// smaller than gravity is nulled (2707-2712); hover moderation puts a linear motor's decay clock on ten seconds when it
/// still moves the vehicle up or down while hover is on (2714-2720); hover is off at a height of zero or below with
/// efficiency zero (1563-1564); the vertical attractor goes dormant once the tilt stops changing (1255-1264). Second Life's
/// behaviour, the default, has none of them. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleInWorldzMotorRulesTests
{
    private const double Deg = Math.PI / 180.0;
    private const double Gravity = 9.80665;
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private static BehaviourRig Rig(VehicleBehaviour b, bool gravity = true)
        => new BehaviourRig(new VehicleSettings { Behaviour = b }, Vehicle.TYPE_CAR, gravity);

    // ---- an upward motor weaker than gravity is nulled ----

    private static BehaviourRig Lifter(VehicleBehaviour b, float motorUp, float timescale, float buoyancy)
    {
        var rig = Rig(b);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, timescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, buoyancy);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(0f, 0f, motorUp));
        return rig;
    }

    // Under InWorldz the linear motor is Halcyon's stepwise ramp (HalcyonLinearReference), which starts a motor at rest at 0.04 m/s
    // and grows its target from there whatever the vehicle does; the rule compares each step's change with gravity.
    // (Second Life's motor pulls by a share of the direction, which is what these four tests used to compare it with.)
    [Fact]
    public void A_first_upward_step_smaller_than_gravity_is_nulled_under_InWorldz_and_Second_Life_lifts()
    {
        const double h = 1.0 / 45;
        var iw = Lifter(VehicleBehaviour.InWorldz, 5f, 1f, 0f);
        var sl = Lifter(VehicleBehaviour.SecondLife, 5f, 1f, 0f);
        var free = Rig(VehicleBehaviour.InWorldz);                   // gravity alone
        var want = new HalcyonLinearReference(new double[] { 0, 0, 5 }, new double[] { 1, 1, 1 }, new double[] { 120, 120, 120 });
        iw.Step(h);
        free.Step(h);
        want.Step(h, -Gravity);
        // The first step asks for 0.04 m/s, far less than gravity: nothing of it passes.
        Assert.Equal(free.Body.LinearVelocity.Z, iw.Body.LinearVelocity.Z, 5);
        Assert.Equal(want.V[2], iw.Body.LinearVelocity.Z, 4);

        // The motor's target goes on growing, until the change it asks for outgrows gravity and passes (as Halcyon's does).
        for (int i = 1; i < 90; i++)
        {
            iw.Step(h);
            free.Step(h);
            want.Step(h, -Gravity);
        }
        Assert.Equal(want.V[2], iw.Body.LinearVelocity.Z, 1);
        Assert.True(iw.Body.LinearVelocity.Z > free.Body.LinearVelocity.Z + 1.0, $"{iw.Body.LinearVelocity.Z} against {free.Body.LinearVelocity.Z}");

        sl.Run(h, 45);
        var slFree = Rig(VehicleBehaviour.SecondLife);
        slFree.Run(h, 45);
        Assert.True(sl.Body.LinearVelocity.Z > slFree.Body.LinearVelocity.Z + 1.0, $"{sl.Body.LinearVelocity.Z} against {slFree.Body.LinearVelocity.Z}");
    }

    // A change bigger than gravity passes with gravity taken off it: a vehicle falling at 50 m/s, a 200 m/s motor starting at
    // 0.04 m/s, changes the velocity by 50.04 m/s, of which 9.807 m/s are held back.
    [Fact]
    public void A_big_upward_change_passes_less_gravity()
    {
        const double h = 1.0 / 45;
        var iw = Lifter(VehicleBehaviour.InWorldz, 200f, 0.05f, 0f);
        iw.Body.LinearVelocity = new Vector3(0f, 0f, -50f);
        iw.Step(h);
        Assert.Equal(-50.0 + (50.04 - Gravity) - Gravity * h, iw.Body.LinearVelocity.Z, 2);
    }

    // The share gravity has is the share buoyancy does not carry: with buoyancy 1 nothing is nulled (the 0.04 m/s of the first
    // step is whole), with 0.5 half of it is.
    [Fact]
    public void The_nulled_gravity_is_the_share_buoyancy_does_not_carry()
    {
        const double h = 1.0 / 45;
        var full = Lifter(VehicleBehaviour.InWorldz, 5f, 1f, 1f);
        full.Step(h);
        Assert.Equal(0.04, full.Body.LinearVelocity.Z, 4);

        var none = Lifter(VehicleBehaviour.InWorldz, 200f, 0.05f, 0f);
        var half = Lifter(VehicleBehaviour.InWorldz, 200f, 0.05f, 0.5f);
        none.Body.LinearVelocity = new Vector3(0f, 0f, -50f);
        half.Body.LinearVelocity = new Vector3(0f, 0f, -50f);
        none.Step(h);
        half.Step(h);
        // Half of gravity held back less, and half of the engine's gravity over the step less taken.
        Assert.Equal(Gravity * 0.5 + Gravity * 0.5 * h, half.Body.LinearVelocity.Z - none.Body.LinearVelocity.Z, 1);
    }

    [Fact]
    public void A_downward_motor_is_left_alone()
    {
        // (A motor under the fall's speed would pull the vehicle up against it, which is an upward change like any other.)
        const double h = 1.0 / 45;
        var iw = Lifter(VehicleBehaviour.InWorldz, -50f, 1f, 0f);
        var want = new HalcyonLinearReference(new double[] { 0, 0, -50 }, new double[] { 1, 1, 1 }, new double[] { 120, 120, 120 });
        for (int i = 0; i < 90; i++)
        {
            iw.Step(h);
            want.Step(h, -Gravity);
        }
        Assert.Equal(want.V[2], iw.Body.LinearVelocity.Z, 1);
        Assert.True(iw.Body.LinearVelocity.Z < -20.0, $"{iw.Body.LinearVelocity.Z}");
    }

    // ---- hover moderation ----

    private static BehaviourRig Hovering(VehicleBehaviour b, int flags, float hoverTimescale, Vector3 motor)
    {
        var rig = Rig(b, gravity: false);
        rig.Car.ProcessVehicleFlags(flags, remove: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 100f);              // where the body is: hover has nothing to do
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, hoverTimescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, motor);
        return rig;
    }

    private const int GlobalHeight = 0x10, WaterOnly = 0x4, TerrainOnly = 0x8, UpOnly = 0x20;

    [Theory]
    [InlineData(GlobalHeight)]
    [InlineData(WaterOnly)]
    [InlineData(TerrainOnly)]
    public void A_motor_that_moves_the_vehicle_up_or_down_has_its_decay_put_on_ten_seconds_while_hover_is_on(int flag)
    {
        var iw = Hovering(VehicleBehaviour.InWorldz, flag, 2f, new Vector3(0f, 0f, -5f));
        var sl = Hovering(VehicleBehaviour.SecondLife, flag, 2f, new Vector3(0f, 0f, -5f));
        iw.Car.Step(1f / 45);
        sl.Car.Step(1f / 45);
        Assert.True(iw.Car.LinearMotorAge >= VehicleController.HoverModerationSeconds, $"{iw.Car.LinearMotorAge}");
        Assert.True(sl.Car.LinearMotorAge < 0.1f, $"{sl.Car.LinearMotorAge}");
    }

    [Fact]
    public void Hover_moderation_needs_a_hover_flag_a_hover_timescale_and_a_motor_that_moves_it_vertically()
    {
        var noFlag = Hovering(VehicleBehaviour.InWorldz, UpOnly, 2f, new Vector3(0f, 0f, -5f));          // up-only is not one of the three
        var off = Hovering(VehicleBehaviour.InWorldz, GlobalHeight, 1000f, new Vector3(0f, 0f, -5f));       // hover timescale off
        var flat = Hovering(VehicleBehaviour.InWorldz, GlobalHeight, 2f, new Vector3(5f, 0f, 0f));          // no vertical push
        foreach (var r in new[] { noFlag, off, flat })
            r.Car.Step(1f / 45);
        Assert.True(noFlag.Car.LinearMotorAge < 0.1f);
        Assert.True(off.Car.LinearMotorAge < 0.1f);
        Assert.True(flat.Car.LinearMotorAge < 0.1f);
    }

    // ---- hover off at height <= 0 with efficiency 0 ----

    [Fact]
    public void A_hover_of_height_zero_and_efficiency_zero_is_no_hover_under_InWorldz()
    {
        foreach (var behaviour in new[] { VehicleBehaviour.InWorldz, VehicleBehaviour.SecondLife })
        {
            var rig = Rig(behaviour, gravity: false);
            rig.Car.ProcessVehicleFlags(UpOnly, remove: true);          // the car's hover is up-only, which does nothing from above
            rig.Car.ProcessVehicleFlags(GlobalHeight, remove: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 0f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1f);
            rig.Run(1.0 / 45, 45);
            // With hover on, a vehicle 100 m above its height has something to do (it is not idle); with it off it has not.
            Assert.Equal(behaviour == VehicleBehaviour.InWorldz, rig.Car.IsIdle);
        }
    }

    [Fact]
    public void A_hover_that_was_on_has_its_timescale_put_off_when_the_legacy_rule_clears_it()
    {
        var iw = Rig(VehicleBehaviour.InWorldz);
        var sl = Rig(VehicleBehaviour.SecondLife);
        foreach (var r in new[] { iw, sl })
        {
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.5f);
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1f);
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0f);
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 0f);          // height <= 0 and efficiency 0: cleared
        }
        Assert.Equal(300f, iw.Car.GetFloatParam(VehFloatParam.HoverTimescale));
        Assert.Equal(1f, sl.Car.GetFloatParam(VehFloatParam.HoverTimescale));
        // and a later height and efficiency do not bring it back: the script has to set the timescale again.
        iw.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        iw.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.5f);
        Assert.Equal(300f, iw.Car.GetFloatParam(VehFloatParam.HoverTimescale));
    }

    [Fact]
    public void A_hover_that_was_not_on_keeps_its_timescale()
    {
        var fresh = Rig(VehicleBehaviour.InWorldz);
        fresh.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 0f);
        fresh.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 20f);       // set last, with height 0 and efficiency 0: not on, so nothing is cleared
        Assert.Equal(20f, fresh.Car.GetFloatParam(VehFloatParam.HoverTimescale));
    }

    // ---- the attractor's dormancy ----

    private static BehaviourRig Righting(VehicleBehaviour b, double rollDegrees, float timescale = 1f)
    {
        var rig = Rig(b, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, timescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
        rig.Tilt(rollDegrees, 0);
        return rig;
    }

    // How much the angular velocity changes over `steps` steps from here: the attractor changes it, and nothing
    // else does (no friction, no motor, no gravity).
    private static float Change(BehaviourRig rig, double h, int steps)
    {
        Vector3 before = rig.Body.AngularVelocity;
        rig.Run(h, steps);
        return (rig.Body.AngularVelocity - before).Length();
    }

    [Fact]
    public void A_slowly_tilting_vehicle_stops_being_righted_after_one_and_a_half_seconds_under_InWorldz()
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            int At(double seconds) => (int)Math.Round(rate * seconds);
            // A 5 s timescale: a 3 degree tilt takes more than 1.5 s to right itself, and moves under 0.7 degree a step.
            var iw = Righting(VehicleBehaviour.InWorldz, 3, 5f);
            var sl = Righting(VehicleBehaviour.SecondLife, 3, 5f);
            iw.Run(h, At(1.0));
            sl.Run(h, At(1.0));
            Assert.True(Change(iw, h, At(1.2) - At(1.0)) > 1e-4f, $"{rate} Hz: still righting at 1 s");
            Change(sl, h, At(1.2) - At(1.0));
            iw.Run(h, At(1.6) - At(1.2));                  // to 1.6 s: past the 1.5 s
            sl.Run(h, At(1.6) - At(1.2));
            float dormantChange = Change(iw, h, At(1.9) - At(1.6));
            Assert.True(dormantChange < 1e-5f, $"{rate} Hz: changed by {dormantChange}");          // dormant: nothing changes the angular velocity
            Assert.True(Change(sl, h, At(1.9) - At(1.6)) > 1e-4f, $"{rate} Hz: Second Life's attractor goes on");
        }
    }

    // A vehicle turned over by 0.6 pi or more is never let go dormant.
    [Fact]
    public void An_overturned_vehicle_is_never_dormant()
    {
        const double h = 1.0 / 45;
        // Held in place (the angular velocity cleared each step), the tilt does not change, as the dormancy rule watches it.
        float Push(double rollDegrees)
        {
            var rig = Righting(VehicleBehaviour.InWorldz, rollDegrees);
            for (int i = 0; i < 90; i++)
            {
                rig.Car.Step((float)h);
                rig.Body.AngularVelocity = Vector3.Zero;
            }
            rig.Car.Step((float)h);
            return rig.Body.AngularVelocity.Length();
        }
        Assert.True(Push(120) > 1e-3f, "overturned: still acting after 2 s without a change");
        Assert.Equal(0f, Push(80));                       // under 0.6 pi: dormant after 1.5 s without a change
    }

    // A vehicle that is level and still starts dormant (nothing to right); one tilted past the 0.7 degree threshold wakes it.
    [Fact]
    public void A_level_vehicle_is_dormant_and_a_tilt_wakes_it()
    {
        var level = Righting(VehicleBehaviour.InWorldz, 0.3);              // under pi / 256 (0.703 degrees) from where it went to sleep
        Assert.Equal(0f, Change(level, 1.0 / 45, 20), 6);
        var tilted = Righting(VehicleBehaviour.InWorldz, 3);
        Assert.True(Change(tilted, 1.0 / 45, 20) > 1e-4f);
    }
}
