/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// VEHICLE_LINEAR_MOTOR_OFFSET turns the vehicle as a push at that point would (dw = I^-1 (r x m dv)), and
/// VEHICLE_FLAG_LIMIT_ROLL_ONLY leaves the pitch to itself for every type, at the four heartbeat rates. Pure (a body
/// that holds what the controller sets and turns by its angular velocity), parallel.
/// </summary>
public class VehicleOffsetAndRollOnlyTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    /// <summary>A body with no gravity or contacts: it keeps what the controller sets and turns by its angular velocity.</summary>
    private sealed class FreeBody : IVehicleBody
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
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;

        // The engine's step for this body: turn by the world angular velocity over h (the turn applied after the
        // present orientation; done in System.Numerics, whose axis-angle rotation keeps the small turns of a short step).
        public void Turn(double h)
        {
            Vector3 w = AngularVelocity;
            float speed = w.Length();
            if (speed <= 0f)
                return;
            var turn = System.Numerics.Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(w.X, w.Y, w.Z) / speed, (float)(speed * h));
            var now = new System.Numerics.Quaternion(Orientation.X, Orientation.Y, Orientation.Z, Orientation.W);
            var next = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(now, turn));
            Orientation = new Quaternion(next.X, next.Y, next.Z, next.W);
        }
    }

    private static (FreeBody body, VehicleController car, Func<double> clock, Action<double> advance) Rig()
    {
        var body = new FreeBody();
        double now = 0;
        var car = new VehicleController(body);
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        // Only what a test sets acts: no attractor, banking, deflection or friction, and no motor-up limit.
        car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f));
        car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
        car.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, true);
        return (body, car, () => now, h => now += h);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Motor offset

    // The motor pushing forward with the body held level (no turning applied): the angular velocity it gives is the
    // angular impulse r x m v over the inertia, at every rate, where v is the motor's velocity.
    [Theory]
    [InlineData(0f, 0f, -0.5f)]   // below the centre: nose up (negative about y)
    [InlineData(0f, 0.4f, 0f)]    // to the left: turns right (negative about z)
    [InlineData(0.7f, 0f, 0f)]    // on the axis of the push: no turn
    public void The_offset_motor_turns_the_body_by_its_angular_impulse(float rx, float ry, float rz)
    {
        foreach (double rate in Rates)
        {
            var (body, car, _, advance) = Rig();
            car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, new Vector3(rx, ry, rz));
            car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(6f, 0f, 0f));
            double h = 1.0 / rate;
            for (int i = 0; i < (int)Math.Round(2.0 * rate); i++)
            {
                car.Step((float)h);
                advance(h);
            }
            float v = body.LinearVelocity.X;
            Assert.True(v > 3f, $"{rate} Hz: the motor gave {v}");
            Vector3 expected = Vector3.Cross(new Vector3(rx, ry, rz), new Vector3(1000f * v, 0f, 0f));
            expected = new Vector3(expected.X / 200f, expected.Y / 400f, expected.Z / 500f);
            Vector3 got = body.AngularVelocity;
            Assert.True(Vector3.Distance(got, expected) < 1e-3f * Math.Max(1f, expected.Length()), $"{rate} Hz: {got} against {expected}");
        }
    }

    [Fact]
    public void With_no_offset_the_motor_does_not_turn_the_body()
    {
        var (body, car, _, advance) = Rig();
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(6f, 0f, 0f));
        for (int i = 0; i < 22; i++) { car.Step(1f / 11f); advance(1.0 / 11.0); }
        Assert.Equal(Vector3.Zero, body.AngularVelocity);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Roll only

    private static (double roll, double pitch) RollAndPitch(Quaternion q)
    {
        Vector3 x = Vector3.UnitX * q, y = Vector3.UnitY * q, z = Vector3.UnitZ * q;
        return (Math.Atan2(y.Z, z.Z), Math.Asin(Math.Clamp(-x.Z, -1f, 1f)));
    }

    // A car rolled 20 and pitched 30 degrees with the attractor at 1 s, efficiency 1, turning freely: after 6 s the
    // roll is gone (the critically damped spring leaves 20 (1 + 6) e^-6 = 0.35 degrees) and, with LIMIT_ROLL_ONLY,
    // the pitch is where it was; without the flag the pitch goes the same way as the roll.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Roll_only_rights_the_roll_and_leaves_the_pitch(bool rollOnly)
    {
        const double deg = Math.PI / 180.0;
        foreach (double rate in Rates)
        {
            var (body, car, _, advance) = Rig();
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1f);
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
            car.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitRollOnly, !rollOnly);
            // Rolled about its own x axis, then pitched about the world's y: the nose 30 degrees down.
            body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(30 * deg)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(20 * deg));
            Assert.Equal(30.0, RollAndPitch(body.Orientation).pitch / deg, 3);
            double h = 1.0 / rate;
            for (int i = 0; i < (int)Math.Round(6.0 * rate); i++)
            {
                car.Step((float)h);
                body.Turn(h);
                advance(h);
            }
            (double roll, double pitch) = RollAndPitch(body.Orientation);
            Assert.True(Math.Abs(roll) < 1.0 * deg, $"{rate} Hz: roll {roll / deg:0.00} degrees");
            if (rollOnly)
                Assert.True(Math.Abs(pitch - 30 * deg) < 0.5 * deg, $"{rate} Hz: pitch {pitch / deg:0.00} degrees, expected 30");
            else
                Assert.True(Math.Abs(pitch) < 1.0 * deg, $"{rate} Hz: pitch {pitch / deg:0.00} degrees, expected 0");
        }
    }
}

/// <summary>The same two behaviours on the real engine, at the four heartbeat rates. Serial with the other native tests.</summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleOffsetAndRollOnlyHarnessTests
{
    // The motor-offset box pitches its nose up as the motor builds speed. The turning rate the motor gives it is the
    // same at every rate (within 0.2% of 90 Hz after 2 s). The tilt it reaches agrees within 3.5%: the engine moves
    // a body by the velocity it has after the controller's change at the start of each step, so at 11 Hz the tilt
    // leads by about half a step of turning (1.4 of 47 degrees), as a vehicle's position does under its linear motor.
    [Fact]
    public void The_offset_motor_pitches_the_box_alike_at_every_rate()
    {
        var end = new Dictionary<double, Sample>();
        foreach (double rate in Harness.Harness.Rates)
            end[rate] = Harness.Harness.Run(Harness.Harness.Find("motor-offset"), new HarnessOptions { RateHz = rate }.SecondLife()).Samples[^1];
        Sample reference = end[90.0];
        Assert.True(reference.Tilt > 20f, $"tilt {reference.Tilt}");
        float w = reference.AngularVelocity.Length();
        foreach (double rate in Harness.Harness.Rates)
        {
            Assert.True(Math.Abs(end[rate].AngularVelocity.Length() - w) <= 0.002f * w, $"{rate} Hz: turning {end[rate].AngularVelocity.Length():0.0000} against {w:0.0000}");
            Assert.True(Math.Abs(end[rate].Tilt - reference.Tilt) <= 0.035f * reference.Tilt, $"{rate} Hz: tilt {end[rate].Tilt:0.00} against {reference.Tilt:0.00}");
        }
    }

    // The roll-only car keeps its 30 degree pitch to the end at every rate, its roll gone.
    [Fact]
    public void The_roll_only_car_keeps_its_pitch_at_every_rate()
    {
        foreach (double rate in Harness.Harness.Rates)
        {
            RunResult r = Harness.Harness.Run(Harness.Harness.Find("rollonly"), new HarnessOptions { RateHz = rate }.SecondLife());
            float end = r.Samples[^1].Tilt;
            Assert.True(Math.Abs(end - 30f) < 0.5f, $"{rate} Hz: tilt at the end {end:0.00}, expected the 30 degree pitch alone");
        }
    }
}
