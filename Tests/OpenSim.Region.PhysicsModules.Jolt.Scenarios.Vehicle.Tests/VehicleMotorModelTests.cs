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
/// The linear and angular motors and friction against their equation, on each vehicle axis:
///
///   dv/dt = g(s) (M - v) / Tm  -  v / Tf          g(s) = e^(-s / Td)
///
/// at the four heartbeat rates and a very fine step, each within 1%. The solver alone, and the controller on a body
/// that keeps what the controller sets and moves nowhere (deflection, attractor, banking and hover off), so the
/// motor and friction are the only things changing its velocity. Pure, parallel.
/// </summary>
public class VehicleMotorModelTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };
    private const double FineRate = 2000.0;
    private const double Tolerance = 0.01;

    // Every time below is a whole number of steps at every rate in Rates (2 s is the shortest such span).
    private const float M = 8f;          // linear motor, m/s
    private const float W = 2f;          // angular motor, rad/s

    private static void Close(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= Tolerance * Math.Abs(expected), $"{what}: {actual:0.0000} against {expected:0.0000}");

    // ---------------------------------------------------------------------------------------------------------
    // The solver alone

    private static double Solve(double v, double motor, double tm, double td, double tf, double rate, double seconds, bool motorOn = true)
    {
        double dt = 1.0 / rate, age = motorOn ? 0.0 : double.PositiveInfinity;
        int n = (int)Math.Round(seconds * rate);
        for (int i = 0; i < n; i++)
        {
            v = VehicleMotorSolver.Step(v, motor, tm, td, age, tf, dt);
            age += dt;
        }
        return v;
    }

    // The equation integrated directly (fourth-order Runge-Kutta at 1/20000 s), independent of the solver.
    private static double Integrate(double v, double motor, double tm, double td, double tf, double seconds, bool motorOn = true)
    {
        const double h = 1.0 / 20000.0;
        double kf = VehicleMotorSolver.FrictionRate(tf);
        double F(double s, double x) => (motorOn ? Math.Exp(-s / td) * (motor - x) / tm : 0.0) - kf * x;
        int n = (int)Math.Round(seconds / h);
        for (int i = 0; i < n; i++)
        {
            double s = i * h;
            double k1 = F(s, v), k2 = F(s + h / 2, v + h / 2 * k1), k3 = F(s + h / 2, v + h / 2 * k2), k4 = F(s + h, v + h * k3);
            v += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
        }
        return v;
    }

    private static void SolverCase(double expected, double v0, double motor, double tm, double td, double tf, double seconds, bool motorOn = true)
    {
        Close(expected, Integrate(v0, motor, tm, td, tf, seconds, motorOn), "integrated equation");
        Close(expected, Solve(v0, motor, tm, td, tf, FineRate, seconds, motorOn), "fine step");
        foreach (double rate in Rates)
            Close(expected, Solve(v0, motor, tm, td, tf, rate, seconds, motorOn), $"{rate} Hz");
    }

    [Fact]
    public void Motor_alone_reaches_one_minus_one_over_e_of_its_speed_at_one_timescale()
        => SolverCase(M * (1 - Math.Exp(-1)), 0, M, 2, 1e6, VehicleMotorSolver.FrictionOffTimescale, 2);

    [Fact]
    public void Friction_alone_leaves_one_over_e_of_the_speed_at_one_timescale()
        => SolverCase(M * Math.Exp(-1), M, 0, 1, 1, 2, 2, motorOn: false);

    [Fact]
    public void Motor_and_friction_settle_at_the_steady_speed_of_the_equation()
        => SolverCase(M * 1.0 / (1.0 + 0.5), 0, M, 0.5, 1e6, 1, 10);   // M Tf / (Tf + Tm)

    [Fact]
    public void A_decaying_motor_gives_what_its_fading_grip_gives_at_one_decay_timescale()
        // No friction: v(t) = M (1 - e^(-(Td / Tm)(1 - e^(-t / Td)))); at t = Td the grip is e^-1.
        => SolverCase(M * (1 - Math.Exp(-2 * (1 - Math.Exp(-1)))), 0, M, 1, 2, VehicleMotorSolver.FrictionOffTimescale, 2);

    [Fact]
    public void The_grip_never_cuts_off()
    {
        // Twenty decay timescales after the set the grip is e^-20, and still pulls.
        double v = VehicleMotorSolver.Step(0, M, 0.1, 0.5, 10.0, VehicleMotorSolver.FrictionOffTimescale, 0.1);
        Assert.True(v > 0 && v < 1e-6, $"{v}");
        Assert.Equal(Math.Exp(-1), VehicleMotorSolver.Grip(2.0, 2.0), 12);
        Assert.Equal(0.0, VehicleMotorSolver.Grip(double.PositiveInfinity, 2.0));
    }

    [Fact]
    public void A_span_gives_the_same_velocity_however_it_is_cut_into_steps()
    {
        // One step of 0.3 s against three of 0.1 s, with the motor, its decay and friction all acting.
        double whole = VehicleMotorSolver.Step(1.0, M, 0.4, 0.7, 0.2, 0.9, 0.3);
        double v = 1.0, age = 0.2;
        for (int i = 0; i < 3; i++) { v = VehicleMotorSolver.Step(v, M, 0.4, 0.7, age, 0.9, 0.1); age += 0.1; }
        Assert.Equal(whole, v, 9);
    }

    [Fact]
    public void A_start_speed_shifts_the_velocity_only_by_its_own_decay()
    {
        // The equation is linear: starting at 5 mm/s instead of 0 adds 5 mm/s times e^-(A + t / Tf), nothing more.
        double fromRest = Solve(0, M, 1, 0.5, 1, 45, 2);
        double fromRez = Solve(0.005, M, 1, 0.5, 1, 45, 2);
        Assert.InRange(fromRez - fromRest, 0, 0.005);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The controller

    /// <summary>A vehicle body that only holds what the controller sets: no integration, no contacts.</summary>
    private sealed class HeldBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        // The share of gravity the engine would apply over the next step (the rig applies it, as the engine does).
        public float GravityFactor;
        public void SetGravityFactor(float factor) => GravityFactor = factor;
        public bool HasCollision { get; set; }
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    private sealed class Rig
    {
        public readonly HeldBody Body = new();
        public readonly VehicleController Car;
        private double _now;

        // Some cases use a 1000 s motor decay to stand for a motor that does not fade: longer than the documented
        // 120 s cap a script can set by default, so the rig's region allows it ([Jolt] VehicleMaxDecayTimescale).
        private static readonly VehicleSettings LongDecay = new VehicleSettings { MaxDecayTimescale = 1000f };

        public Rig(float tm, float td, float tf, bool angular)
        {
            Car = new VehicleController(Body) { Settings = LongDecay };
            DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Car.Clock = () => t0.AddTicks((long)(_now * TimeSpan.TicksPerSecond));
            Car.ProcessTypeChange(Vehicle.TYPE_CAR);
            // Only the motor and friction act: deflection, the attractor and banking at their "off" timescales.
            Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
            Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
            Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
            Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1000f);
            Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(angular ? 1000f : tf, 1000f, 1000f));
            Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, angular ? tf : 1000f));
            Car.ProcessFloatVehicleParam(angular ? Vehicle.ANGULAR_MOTOR_TIMESCALE : Vehicle.LINEAR_MOTOR_TIMESCALE, tm);
            Car.ProcessFloatVehicleParam(angular ? Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE : Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, td);
        }

        public void Run(double rate, double seconds)
        {
            int n = (int)Math.Round(seconds * rate);
            for (int i = 0; i < n; i++)
            {
                Car.Step((float)(1.0 / rate));
                // The engine's step: the vehicle's share of gravity over it (there is nothing else to integrate).
                Body.LinearVelocity += Body.Gravity * Body.GravityFactor * (float)(1.0 / rate);
                _now += 1.0 / rate;
            }
        }

        public void Pause(double seconds) => _now += seconds;
    }

    // The velocity on the axis under test after the run: forward (X) for the linear motor, about Z (the steering
    // axis) for the angular one.
    private static double ControllerRun(bool angular, float tm, float td, float tf, float start, float motor, double rate, double seconds)
    {
        var rig = new Rig(tm, td, tf, angular);
        if (angular) rig.Body.AngularVelocity = new Vector3(0f, 0f, start);
        else rig.Body.LinearVelocity = new Vector3(start, 0f, 0f);
        if (motor != 0f)
            rig.Car.ProcessVectorVehicleParam(angular ? Vehicle.ANGULAR_MOTOR_DIRECTION : Vehicle.LINEAR_MOTOR_DIRECTION,
                angular ? new Vector3(0f, 0f, motor) : new Vector3(motor, 0f, 0f));
        rig.Run(rate, seconds);
        return angular ? rig.Body.AngularVelocity.Z : rig.Body.LinearVelocity.X;
    }

    private static void ControllerCase(bool angular, double expected, float tm, float td, float tf, float start, float motor, double seconds)
    {
        string axis = angular ? "angular" : "linear";
        Close(expected, ControllerRun(angular, tm, td, tf, start, motor, FineRate, seconds), $"{axis}, fine step");
        foreach (double rate in Rates)
            Close(expected, ControllerRun(angular, tm, td, tf, start, motor, rate, seconds), $"{axis}, {rate} Hz");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_controllers_motor_alone_at_one_timescale(bool angular)
    {
        float m = angular ? W : M;
        // Decay at its longest (1000 s): over 2 s the grip is still above 0.998, and the closed form includes it.
        double a = 1000.0 / 2.0 * (1 - Math.Exp(-2.0 / 1000.0));
        ControllerCase(angular, m * (1 - Math.Exp(-a)), 2f, 1000f, 1000f, 0f, m, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_controllers_friction_alone_at_one_timescale(bool angular)
    {
        float v0 = angular ? W : M;
        ControllerCase(angular, v0 * Math.Exp(-1), 2f, 1000f, 2f, v0, 0f, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_controllers_motor_and_friction_settle_at_the_steady_speed(bool angular)
    {
        float m = angular ? W : M;
        ControllerCase(angular, m * 1.0 / (1.0 + 0.5), 0.5f, 1000f, 1f, 0f, m, 10);   // M Tf / (Tf + Tm)
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_controllers_decaying_motor_at_one_decay_timescale(bool angular)
    {
        float m = angular ? W : M;
        ControllerCase(angular, m * (1 - Math.Exp(-2 * (1 - Math.Exp(-1)))), 1f, 2f, 1000f, 0f, m, 2);
    }

    [Fact]
    public void Friction_acts_on_the_vertical_axis_too_with_motor_up_limited()
    {
        // The car preset has LIMIT_MOTOR_UP. A car falling at 5 m/s with vertical friction 1 s, under gravity:
        // dv/dt = -v / 1 s - g, so v(t) = -5 e^(-t) - g (1 - e^(-t)), heading for -g * 1 s: the upward change friction
        // makes is not dropped.
        var rig = new Rig(1f, 1000f, 1000f, false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1f));
        rig.Body.LinearVelocity = new Vector3(0f, 0f, -5f);
        rig.Run(45, 2);
        Close(-5 * Math.Exp(-2) - 9.80665 * (1 - Math.Exp(-2)), rig.Body.LinearVelocity.Z, "vertical friction");
    }

    [Fact]
    public void A_motor_does_not_suspend_friction()
    {
        // A motor held at full grip with friction on the same axis settles at M Tf / (Tf + Tm), not at M.
        double v = ControllerRun(false, 1f, 1000f, 1f, 0f, M, 45, 10);
        Assert.True(v < 0.6 * M, $"{v}");
    }

    [Fact]
    public void A_motor_set_to_zero_brakes_toward_zero()
    {
        // No friction: a zero motor with full grip takes the speed down exponentially with the motor timescale.
        var rig = new Rig(1f, 1000f, 1000f, false);
        rig.Body.LinearVelocity = new Vector3(M, 0f, 0f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, Vector3.Zero);
        rig.Run(45, 2);
        double a = 1000.0 * (1 - Math.Exp(-2.0 / 1000.0));
        Close(M * Math.Exp(-a), rig.Body.LinearVelocity.X, "braking");
    }

    [Fact]
    public void After_a_gap_of_over_a_second_the_motors_are_released()
    {
        // A motor set, stepped for a second, then not stepped for 5 s (physics off): its grip would have decayed,
        // so stepping resumes with the motor released and only friction acting.
        var rig = new Rig(1f, 1f, 1000f, false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(M, 0f, 0f));
        rig.Run(45, 1);
        float atPause = rig.Body.LinearVelocity.X;
        rig.Pause(5);
        rig.Run(45, 1);
        Assert.Equal(atPause, rig.Body.LinearVelocity.X, 3);
    }
}

/// <summary>A car's drive under the key through the harness does not depend on the few mm/s a rez leaves it with
/// (serial: real backends on the shared pool).</summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleStartSpeedTests
{
    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void The_pad_drive_is_the_same_from_rest_and_from_a_rez_creep(double rate)
    {
        float Distance(float start)
        {
            var o = new HarnessOptions { RateHz = rate, SlopeDeg = 0f, Hold = 3f, Duration = 4f, StartSpeed = start }.SecondLife();
            return Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary.DistanceBeforeRelease;
        }
        float fromRest = Distance(0f), fromCreep = Distance(0.005f);
        Assert.True(Math.Abs(fromCreep - fromRest) < 0.01f * fromRest, $"{fromCreep:0.000} m against {fromRest:0.000} m");
    }
}
