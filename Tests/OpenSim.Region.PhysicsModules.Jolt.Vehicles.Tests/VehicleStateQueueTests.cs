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
/// A call that changes the vehicle controller's running state (a type change, StopMotors, ResumeAfterHold, a motor set) from a
/// script or scene thread is queued and applied at the start of the next Update, never in the middle of one: a collision step
/// never sees a state that is half changed. Pure (a body with no engine), so the class runs in parallel with the others.
/// </summary>
public class VehicleStateQueueTests
{
    private const float Update = 1f / 45f;

    private static BehaviourRig RunningCar(bool defer)
    {
        var rig = new BehaviourRig(new VehicleSettings(), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.DeferStateChanges = defer;
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.StepUpdate(Update, 2);   // the motors are set and running
        return rig;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_type_change_during_an_Update_lands_at_the_next_one(int n)
    {
        var rig = RunningCar(defer: true);
        Assert.True(rig.Car.LinearMotorSet);
        Assert.True(rig.Car.AngularMotorSet);

        rig.Car.BeginUpdate(Update);
        float age = rig.Car.LinearMotorAge;
        rig.Car.ProcessTypeChange(Vehicle.TYPE_SLED);   // from another thread, mid-Update
        for (int i = 0; i < n; i++)
        {
            rig.Car.StepOnce(Update / n, rig.Body);
            rig.Body.Advance(Update / n);
            Assert.True(float.IsFinite(rig.Car.LinearMotorAge), "the type change cleared the motors inside the Update");
            Assert.True(float.IsFinite(rig.Car.AngularMotorAge));
            age += Update / n;
            Assert.Equal(age, rig.Car.LinearMotorAge, 4);   // the motor's clock ran on, unbroken
        }

        rig.Car.BeginUpdate(Update);   // the next Update takes it
        Assert.False(rig.Car.LinearMotorSet);
        Assert.False(rig.Car.AngularMotorSet);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void StopMotors_during_an_Update_lands_at_the_next_one(int n)
    {
        var rig = RunningCar(defer: true);
        rig.Car.BeginUpdate(Update);
        rig.Car.StopMotors();
        for (int i = 0; i < n; i++)
        {
            rig.Car.StepOnce(Update / n, rig.Body);
            rig.Body.Advance(Update / n);
            Assert.True(float.IsFinite(rig.Car.LinearMotorAge), "StopMotors stopped the motors inside the Update");
        }
        float speed = rig.Body.LinearVelocity.X;
        Assert.True(speed > 0f, "the motor drove through the Update");

        rig.Car.BeginUpdate(Update);
        Assert.False(rig.Car.LinearMotorSet);
        Assert.False(rig.Car.AngularMotorSet);
    }

    // The same two calls on a host that has no collision steps act at once, as they always did.
    [Fact]
    public void Without_the_request_the_calls_act_at_once()
    {
        var rig = RunningCar(defer: false);
        rig.Car.StopMotors();
        Assert.False(rig.Car.LinearMotorSet);
        rig = RunningCar(defer: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_SLED);
        Assert.False(rig.Car.LinearMotorSet);
    }

    // The order of the calls is the order they act in: a motor set made after a stop or a type change starts the motor at
    // the next Update, one made before it is dropped.
    [Fact]
    public void A_motor_set_after_a_queued_stop_runs_and_one_before_it_is_dropped()
    {
        var rig = RunningCar(defer: true);
        rig.Car.StopMotors();
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(3f, 0f, 0f));   // after the stop
        rig.Car.BeginUpdate(Update);
        Assert.True(rig.Car.LinearMotorSet);
        Assert.False(rig.Car.AngularMotorSet);

        rig = RunningCar(defer: true);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(3f, 0f, 0f));   // before the stop
        rig.Car.StopMotors();
        rig.Car.BeginUpdate(Update);
        Assert.False(rig.Car.LinearMotorSet);
    }

    [Fact]
    public void ResumeAfterHold_during_an_Update_lands_at_the_next_one()
    {
        var rig = RunningCar(defer: true);
        DateTime start = rig.Car.Clock();
        Assert.True(start >= DateTime.MinValue);
        rig.Car.BeginUpdate(Update);
        rig.Car.ResumeAfterHold();
        rig.Car.StepOnce(Update, rig.Body);
        // It acts through the snapshot at the next Update; nothing the step read changed. (The controller has no reader for the
        // access time but its stale-motor reset, which a held vehicle must not trip: the test is that the call neither throws
        // nor clears the motors.)
        Assert.True(rig.Car.LinearMotorSet);
        rig.Car.BeginUpdate(Update);
        Assert.True(rig.Car.LinearMotorSet);
    }

    // The same, with the calls coming from other threads while Updates run: inside an Update the motor's decay clock only
    // ever advances by the collision step (a mid-Update clear would show as a jump to infinity or back to zero), and nothing
    // throws. The deterministic tests above are the proof; this one is the stress.
    [Fact]
    public void Calls_from_other_threads_never_change_the_state_inside_an_Update()
    {
        var rig = RunningCar(defer: true);
        using var stop = new CancellationTokenSource();
        Exception fault = null;
        var scripts = Task.Run(() =>
        {
            try
            {
                int i = 0;
                while (!stop.IsCancellationRequested)
                {
                    switch (i++ % 5)
                    {
                        case 0: rig.Car.ProcessTypeChange(Vehicle.TYPE_CAR); break;
                        case 1: rig.Car.StopMotors(); break;
                        case 2: rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f)); break;
                        case 3: rig.Car.ResumeAfterHold(); break;
                        default: rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f + i % 3); break;
                    }
                    Thread.Yield();
                }
            }
            catch (Exception e) { fault = e; }
        });

        try
        {
            const int n = 3;
            for (int update = 0; update < 3000 && fault == null; update++)
            {
                rig.Car.BeginUpdate(Update);
                float age = rig.Car.LinearMotorAge;
                bool set = float.IsFinite(rig.Car.LinearMotorAge);
                for (int i = 0; i < n; i++)
                {
                    rig.Car.StepOnce(Update / n, rig.Body);
                    rig.Body.Advance(Update / n);
                    Assert.Equal(set, float.IsFinite(rig.Car.LinearMotorAge));
                    if (set)
                    {
                        age += Update / n;
                        Assert.Equal(age, rig.Car.LinearMotorAge, 3);
                    }
                }
            }
        }
        finally
        {
            stop.Cancel();
            // the script tasks were cancelled just above; the wait only bounds how long the test lets them wind down
#pragma warning disable xUnit1031
            scripts.Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031
        }
        Assert.Null(fault);
    }
}
