/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How the vehicle controller is split between the Update (BeginUpdate) and the collision steps (StepOnce): what the steps
/// read is fixed when the Update starts, a script's motor set waits for the next Update, a script's type change, stop and resume wait for it too, the wind is taken in every
/// collision step, a torque is a change of angular velocity, and InWorldz's per-step constants take their share of a step.
/// Pure (a body with no engine), so the classes run in parallel.
/// </summary>
public class VehicleStepSplitTests
{
    private const float Update = 1f / 45f;

    // ---- What the steps read is what the Update started with ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_parameter_set_while_the_engine_steps_acts_from_the_next_Update(int n)
    {
        var rig = new BehaviourRig(new VehicleSettings(), Vehicle.TYPE_CAR, gravity: false);
        rig.Body.LinearVelocity = new Vector3(10f, 0f, 0f);

        rig.Car.BeginUpdate(Update);
        // A script sets a 0.5 s friction now, between the Update's start and its collision steps.
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(0.5f, 1000f, 1000f));
        for (int i = 0; i < n; i++)
        {
            rig.Car.StepOnce(Update / n, rig.Body);
            rig.Body.Advance(Update / n);
        }
        float afterFirst = rig.Body.LinearVelocity.X;
        Assert.True(afterFirst > 9.99f, $"the friction set during the Update acted in it: {afterFirst}");

        rig.Car.BeginUpdate(Update);
        for (int i = 0; i < n; i++)
        {
            rig.Car.StepOnce(Update / n, rig.Body);
            rig.Body.Advance(Update / n);
        }
        // e^(-h / 0.5) of it is left after one Update's time under the friction.
        Assert.Equal(afterFirst * Math.Exp(-Update / 0.5), rig.Body.LinearVelocity.X, 3);
    }

    [Fact]
    public void A_motor_set_waits_for_the_next_Update_when_the_host_asks_it_to()
    {
        var rig = new BehaviourRig(new VehicleSettings(), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.DeferStateChanges = true;
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        Assert.False(rig.Car.LinearMotorSet, "the motor was set at once");
        Assert.Equal(new Vector3(8f, 0f, 0f), rig.Car.GetVecParam(VehVectorParam.LinearMotorDirection));   // the setting itself is read at once

        rig.Car.BeginUpdate(Update);
        Assert.True(rig.Car.LinearMotorSet);
        Assert.Equal(0f, rig.Car.LinearMotorAge);
        rig.Car.StepOnce(Update, rig.Body);
        Assert.True(rig.Body.LinearVelocity.X > 0f);

        // Without the request (a host with no collision steps) it acts at once, as it always did.
        var plain = new BehaviourRig(new VehicleSettings(), Vehicle.TYPE_CAR, gravity: false);
        plain.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        Assert.True(plain.Car.LinearMotorSet);
    }

    [Fact]
    public void A_type_change_drops_a_motor_set_that_was_waiting()
    {
        var rig = new BehaviourRig(new VehicleSettings(), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.DeferStateChanges = true;
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        rig.Car.ProcessTypeChange(Vehicle.TYPE_SLED);
        rig.Car.BeginUpdate(Update);
        Assert.False(rig.Car.LinearMotorSet, "a motor set from before the type change started the new type's motor");
    }

    // ---- The wind in every collision step ----

    private static BehaviourRig WindRig()
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz }, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessVehicleFlags(0x20000, false);   // VEHICLE_FLAG_REACT_TO_WIND, as a script numbers it
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(0.5f, 0.5f, 0f));
        rig.Body.WindAt = _ => new Vector3(5f, 0f, 0f);
        return rig;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_wind_is_sampled_in_every_collision_step(int n)
    {
        var rig = WindRig();
        for (int i = 0; i < 16; i++)
            rig.StepUpdate(Update, n);
        Assert.Equal(16 * n, rig.Body.WindReads);
    }

    // A constant wind pushes the vehicle wind x efficiency x time from the first step: 5 m/s x 0.5 x 1 s.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_steady_wind_pushes_a_vehicle_from_the_first_step_however_the_Update_is_split(int n)
    {
        var rig = WindRig();
        for (int i = 0; i < 45; i++)
            rig.StepUpdate(Update, n);
        Assert.InRange(rig.Body.LinearVelocity.X, 2.5 * 0.99, 2.5 * 1.01);
    }

    // ---- A torque is a change of angular velocity ----

    private sealed class CountingBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(200f, 400f, 500f);
        public Vector3 Gravity => Vector3.Zero;
        public void SetGravityFactor(float factor) { }
        public bool HasCollision => false;
        public int TorqueCalls, ForceCalls;
        public void AddForce(Vector3 force) => ForceCalls++;
        public void AddTorque(Vector3 torque) => TorqueCalls++;
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_torque_is_handed_to_the_body_as_the_change_of_angular_velocity_it_makes(int n)
    {
        var body = new CountingBody();
        var car = new VehicleController(body);
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.BeginUpdate(Update);
        float sub = Update / n;
        car.StepOnce(sub, body);
        body.AngularVelocity = Vector3.Zero;
        // A torque added during a step (nothing in the controller adds one today), applied at the end of it.
        car.AddTorqueImpulse(new Vector3(0f, 0f, 5f));
        typeof(VehicleController).GetMethod("TorqueFini", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(car, null);
        Assert.Equal(0, body.TorqueCalls);   // never as a torque: the engine would apply it again in each later collision step
        Assert.Equal(5f / 500f * sub, body.AngularVelocity.Z, 6);
    }

    // ---- InWorldz's per-step constants take their share of a step ----

    // The share of the roll and pitch rate its attractor takes away is Halcyon's per step. Run in n collision steps of an
    // Update it takes the share n times in smaller parts, which compound to the one whole share, so the Update's total is what it
    // was when the controller ran once.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InWorldz_attractor_damping_takes_the_same_share_of_an_Update_in_any_number_of_collision_steps(int n)
    {
        float AfterOneUpdate(int steps)
        {
            var rig = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz }, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 490f);   // on, and its push too small to matter
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.5f);
            rig.Tilt(5, 0);
            rig.Body.AngularVelocity = Vector3.UnitX * rig.Body.Orientation * 0.5f;
            rig.StepUpdate(Update, steps);
            return rig.Body.AngularVelocity.Length();
        }
        float whole = AfterOneUpdate(1);
        Assert.True(whole < 0.5f * 0.85f, $"the damping took nothing: {whole}");
        Assert.Equal(whole, AfterOneUpdate(n), 3);
    }
}
