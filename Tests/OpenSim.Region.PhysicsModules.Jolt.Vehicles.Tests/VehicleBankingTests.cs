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
/// Angular deflection and banking on the controller alone, at the four rates against their exact solutions: a body
/// that keeps the velocities the controller sets, and turns (or not) by its angular velocity. Pure, parallel.
/// </summary>
public class VehicleBankingTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private sealed class Body : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public void SetGravityFactor(float factor) { }
        public bool HasCollision { get; set; }
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A car with everything that would act here off but the behaviour under test; motors released.
    private static (VehicleController car, Body body, Action<double> step) Car(Vector3 velocity, Quaternion orientation, bool turns,
                                                                         bool touching = false)
    {
        var body = new Body { LinearVelocity = velocity, Orientation = orientation, HasCollision = touching };
        var car = new VehicleController(body);
        double now = 0, yaw = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        now = 2.0;   // the first step finds the motors stale and releases them
        void Step(double h)
        {
            car.Step((float)h);
            if (turns)
            {
                // The engine's step: the body turns by its angular velocity over the step (a yaw here, kept in double).
                yaw += body.AngularVelocity.Z * h;
                body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw);
            }
            now += h;
        }
        return (car, body, Step);
    }

    // Angular deflection: the nose turns toward a fixed velocity, the angle between them decaying as
    // e^(-eff (speed / 30 m/s) t / T).
    // (The body turns by yaw alone here; the angle is measured in floats, so to 2e-4.)
    [Theory]
    [InlineData(1f, 2f, 5f)]
    [InlineData(0.5f, 1f, 5f)]
    [InlineData(1f, 0.5f, 25f)]
    public void Angular_deflection_turns_the_nose_to_the_velocity_exactly(float efficiency, float timescale, float speed)
    {
        Vector3 velocity = new Vector3(4f, 3f, 0f) * (speed / 5f);
        foreach (double rate in Rates)
        {
            var (car, body, step) = Car(velocity, Quaternion.Identity, turns: true);
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
            car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, efficiency);
            car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, timescale);
            double a0 = Math.Atan2(3, 4);
            for (int i = 1; i <= (int)Math.Round(3 * rate); i++)
            {
                step(1.0 / rate);
                Vector3 nose = Vector3.UnitX * body.Orientation;
                double angle = Math.Acos(Math.Clamp(Vector3.Dot(nose, Vector3.Normalize(in velocity)), -1f, 1f));
                double expected = a0 * Math.Exp(-efficiency * (speed / 30.0) * (i / rate) / timescale);
                Assert.True(Math.Abs(angle - expected) < 2e-4, $"{rate} Hz at {i / rate:0.000} s (step {i}): {angle} against {expected}");
            }
        }
    }

    // Banking: rolled 0.3 rad and held there, the yaw rate approaches the banking target with the banking timescale:
    // w(t) = target (1 - e^(-t / T)), target = -roll * eff * pi * ((1 - mix) + mix * speed / 30 m/s), the roll the angle
    // (Linden_Vehicle_Tutorial, "Banking": "proportional to the VEHICLE_BANKING_EFFICIENCY, the angle of the roll
    // rotation"). On the ground: the car's LIMIT_MOTOR_UP fades banking in the air (below).
    [Theory]
    [InlineData(1f, 0f, 0f, 1f)]      // static banking at rest
    [InlineData(-0.2f, 0f, 0f, 1f)]   // the car preset's efficiency: leans out, turns the other way
    [InlineData(1f, 1f, 10f, 0.5f)]   // dynamic banking at 10 m/s
    [InlineData(0.5f, 0.5f, 10f, 2f)] // half and half
    public void Banking_turns_the_yaw_rate_toward_its_target_exactly(float efficiency, float mix, float speed, float timescale)
    {
        const float Roll = 0.3f;
        Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Roll);
        double target = -Roll * efficiency * Math.PI * ((1 - mix) + mix * speed / 30.0);
        foreach (double rate in Rates)
        {
            var (car, body, step) = Car(new Vector3(speed, 0f, 0f) * rolled, rolled, turns: false, touching: true);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, efficiency);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, mix);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, timescale);
            // The attractor must be on for banking; with no damping its spring acts about the vehicle's own x axis
            // (level here) only, and leaves the yaw to banking.
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
            for (int i = 1; i <= (int)Math.Round(3 * rate); i++)
            {
                step(1.0 / rate);
                double expected = target * (1 - Math.Exp(-(i / rate) / timescale));
                Assert.True(Math.Abs(body.AngularVelocity.Z - expected) < 1e-4, $"{rate} Hz at {i / rate:0.000} s: {body.AngularVelocity.Z} against {expected}");
            }
        }
    }

    // The roll is the angle about the vehicle's own x axis, as the vertical attractor measures it, whatever the pitch: a
    // car climbing 0.5 rad nose up and rolled 0.6 rad banks as one rolled 0.6 rad on the level. Upside down (rolled
    // 2.5 rad), the roll is measured from the nearer side, pi - 2.5, and turns the other way, as before.
    // The attractor is on (it must be for banking) but so slow (400 s, no damping) that its roll spring, about the
    // climbing car's tilted x axis, adds under 1e-5 rad/s about world z.
    [Theory]
    [InlineData(0.6f, 0.5f, 0.6)]
    [InlineData(2.5f, 0f, -(Math.PI - 2.5))]
    public void Banking_follows_the_roll_angle_whatever_the_pitch(float roll, float pitch, double bankRoll)
    {
        const float Efficiency = 1f, Mix = 0.5f, Speed = 10f, Timescale = 1f;
        // Rolled about its own x axis, then pitched about the world's y (negative: nose up).
        Quaternion pose = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -pitch) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, roll);
        Vector3 y = Vector3.UnitY * pose, z = Vector3.UnitZ * pose;
        Assert.Equal(roll, Math.Atan2(y.Z, z.Z), 4);
        double target = -bankRoll * Efficiency * Math.PI * ((1 - Mix) + Mix * Speed / 30.0);
        foreach (double rate in Rates)
        {
            var (car, body, step) = Car(Vector3.UnitX * Speed * pose, pose, turns: false, touching: true);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, Efficiency);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, Mix);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, Timescale);
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 400f);
            for (int i = 1; i <= (int)Math.Round(3 * rate); i++)
            {
                step(1.0 / rate);
                double expected = target * (1 - Math.Exp(-(i / rate) / Timescale));
                Assert.True(Math.Abs(body.AngularVelocity.Z - expected) < 1e-4, $"{rate} Hz at {i / rate:0.000} s: {body.AngularVelocity.Z} against {expected}");
            }
        }
    }

    // VEHICLE_FLAG_LIMIT_MOTOR_UP (llSetVehicleFlags): "the strength of the banking will decay when the vehicle no
    // longer experiences collisions. The decay timescale is the same as VEHICLE_BANKING_TIMESCALE." In the air the
    // strength is e^(-t_air / T), so the yaw rate follows w' = (A e^(-t / T) - w) / T: w(t) = A (t / T) e^(-t / T) from
    // rest. Touching again, the banking is whole: w' = (A - w) / T from where it was. Without the flag the banking keeps
    // its strength in the air.
    [Fact]
    public void With_limit_motor_up_banking_fades_in_the_air_on_the_banking_timescale()
    {
        const float Roll = 0.3f, Timescale = 1f;
        Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Roll);
        double full = -Roll * Math.PI;   // efficiency 1, static banking
        foreach (bool flag in new[] { true, false })
            foreach (double rate in Rates)
            {
                var (car, body, step) = Car(Vector3.Zero, rolled, turns: false, touching: false);
                car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
                car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 0f);
                car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, Timescale);
                car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
                if (!flag)
                    car.ProcessVehicleFlags(0x40, remove: true);
                Assert.Equal(flag, (car.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0);
                int land = (int)Math.Round(1.5 * rate);
                double tLand = land / rate;
                double wLand = flag ? full * (tLand / Timescale) * Math.Exp(-tLand / Timescale) : full * (1 - Math.Exp(-tLand / Timescale));
                for (int i = 1; i <= (int)Math.Round(4 * rate); i++)
                {
                    step(1.0 / rate);
                    double t = i / rate;
                    double expected = i <= land
                        ? (flag ? full * (t / Timescale) * Math.Exp(-t / Timescale) : full * (1 - Math.Exp(-t / Timescale)))
                        : full + (wLand - full) * Math.Exp(-(t - tLand) / Timescale);
                    Assert.True(Math.Abs(body.AngularVelocity.Z - expected) < 1e-4,
                                $"flag {flag}, {rate} Hz at {t:0.000} s: {body.AngularVelocity.Z} against {expected}");
                    if (i == land)
                        body.HasCollision = true;
                }
            }
    }

    [Fact]
    public void Without_the_vertical_attractor_there_is_no_banking()
    {
        Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var (car, body, step) = Car(Vector3.Zero, rolled, turns: false);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1f);
        car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        for (int i = 0; i < 45; i++)
            step(1.0 / 45);
        Assert.Equal(0f, body.AngularVelocity.Z);
    }
}
