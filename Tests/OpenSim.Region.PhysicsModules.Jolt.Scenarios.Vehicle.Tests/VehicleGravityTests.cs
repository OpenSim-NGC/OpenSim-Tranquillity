/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle's gravity inside the motor and friction equation, dv/dt = g(s) (M - v) / Tm - v / Tf + a: the
/// solver's step against a fine integration, and a car driven down a ramp settling at the equation's speed at every
/// rate. Serial with the other native tests: the harness runs step a real backend.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleGravityTests
{
    private const double G = 9.80665;

    // dv/dt = grip (M - v) / Tm - v / Tf + a by RK4 at a fine step: the reference.
    private static double Integrate(double v0, double m, double tm, double td, double age, double tf, double a, double seconds)
    {
        const int Steps = 20000;
        double h = seconds / Steps, v = v0, t = 0;
        double kf = tf >= VehicleMotorSolver.FrictionOffTimescale ? 0 : 1 / tf;
        double F(double tt, double vv) => (double.IsPositiveInfinity(age) ? 0 : Math.Exp(-(age + tt) / td)) * (m - vv) / tm - kf * vv + a;
        for (int i = 0; i < Steps; i++)
        {
            double k1 = F(t, v), k2 = F(t + h / 2, v + h / 2 * k1), k3 = F(t + h / 2, v + h / 2 * k2), k4 = F(t + h, v + h * k3);
            v += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
            t += h;
        }
        return v;
    }

    [Theory]
    [InlineData(0.0, 8.0, 1.0, 0.5, 0.0, 1.0, -2.5)]                       // motor, friction and gravity
    [InlineData(3.0, 8.0, 0.5, 10.0, 0.2, 100.0, 2.54)]                    // the car preset's forward axis on 15 degrees
    [InlineData(3.0, 0.0, 1.0, 1.0, double.PositiveInfinity, 1.0, -9.80665)] // no motor: friction and gravity
    [InlineData(-4.0, 0.0, 1.0, 1.0, double.PositiveInfinity, 1000.0, -9.80665)] // free fall
    [InlineData(2.0, 5.0, 0.2, 0.3, 0.0, 1000.0, -9.80665)]                // motor and gravity, no friction
    public void A_step_with_gravity_is_the_exact_solution(double v0, double m, double tm, double td, double age, double tf, double a)
    {
        foreach (double rate in new[] { 11.0, 22.5, 45.0, 90.0 })
        {
            double h = 1.0 / rate;
            double expected = Integrate(v0, m, tm, td, age, tf, a, h);
            double got = VehicleMotorSolver.Step(v0, m, tm, td, age, tf, h, a);
            Assert.True(Math.Abs(got - expected) < 1e-6 * Math.Max(1.0, Math.Abs(expected)), $"{rate} Hz: {got} against {expected}");
        }
    }

    [Fact]
    public void A_span_cut_into_steps_ends_where_one_step_does()
    {
        // 1 s of the car preset's forward axis on 15 degrees, as one step and as 11, 45 and 90 steps.
        double one = VehicleMotorSolver.Step(1.0, 8.0, 0.5, 10.0, 0.0, 100.0, 1.0, 2.54);
        foreach (int n in new[] { 11, 45, 90 })
        {
            double v = 1.0;
            for (int i = 0; i < n; i++)
                v = VehicleMotorSolver.Step(v, 8.0, 0.5, 10.0, (double)i / n, 100.0, 1.0 / n, 2.54);
            Assert.True(Math.Abs(v - one) < 1e-6, $"{n} steps: {v} against {one}");
        }
    }

    // The equation's steady speed along a ramp of the given angle, for a car facing down it with its key held (the
    // motor re-set every KeyRepeat, on the heartbeat at or after each repeat, as the harness's heartbeat feed does):
    // the mean over the last 10 s of a 30 s fine integration, at the heartbeat sample times. With LIMIT_MOTOR_UP a
    // motor pushing back up the ramp (braking a car that runs faster than its motor) loses the upward part of its
    // push, which leaves cos^2 of it along the ramp.
    private static double EquationSteady(double rate, double m, double tm, double td, double tf, double slopeDeg, bool limitMotorUp)
    {
        double dt = 1.0 / rate, th = slopeDeg * Math.PI / 180.0, a = G * Math.Sin(th), up = Math.Cos(th) * Math.Cos(th);
        const int PerStep = 200;
        double v = 0, nextKey = 0, setAt = 0, sum = 0;
        int n = 0;
        for (int k = 0; k * dt < 30.0; k++)
        {
            double now = k * dt;
            if (now >= nextKey) { setAt = now; nextKey += 0.1f; }
            double h = dt / PerStep, t = now;
            double F(double tt, double vv)
            {
                double motor = Math.Exp(-(tt - setAt) / td) * (m - vv) / tm;
                if (limitMotorUp && motor < 0) motor *= up;
                return motor - vv / tf + a;
            }
            for (int i = 0; i < PerStep; i++)
            {
                double k1 = F(t, v), k2 = F(t + h / 2, v + h / 2 * k1), k3 = F(t + h / 2, v + h / 2 * k2), k4 = F(t + h, v + h * k3);
                v += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
                t += h;
            }
            if ((k + 1) * dt > 20.0) { sum += v; n++; }
        }
        return sum / n;
    }

    // The test car (motor timescale 1, decay 0.5, friction 1 s along) and the car preset (legacy 0.5, 10, 100 s;
    // documented 1, 60, 100 s), key held down the ramp: the speed on the lower ramp is the equation's, within 1%, at
    // every rate.
    [Theory]
    [InlineData("testcar-down", 5f, 1.0, 0.5, 1.0, "documented")]
    [InlineData("testcar-down", 15f, 1.0, 0.5, 1.0, "documented")]
    [InlineData("testcar-down", 5f, 1.0, 0.5, 1.0, "legacy")]
    [InlineData("testcar-down", 15f, 1.0, 0.5, 1.0, "legacy")]
    [InlineData("car-down", 5f, 1.0, 60.0, 100.0, "documented")]
    [InlineData("car-down", 15f, 1.0, 60.0, 100.0, "documented")]
    [InlineData("car-down", 5f, 0.5, 10.0, 100.0, "legacy")]
    [InlineData("car-down", 15f, 0.5, 10.0, 100.0, "legacy")]
    public void A_car_driven_down_a_ramp_settles_at_the_equations_speed(string scenario, float slope, double tm, double td, double tf, string presets)
    {
        foreach (double rate in Harness.Harness.Rates)
        {
            // This checks the vehicle's equation, so the engine's contact solving is kept the same at every rate: at
            // 90 Hz the engine takes 3 collision steps per step, the 3.7 ms collision step it takes at 45 Hz with its
            // default 6. With 6 at 90 Hz (1.85 ms) the documented test car on 5 degrees runs 1.1% (Windows) to 1.7%
            // (Linux) under the equation, in whichever pose the contact leaves it, and on the equation with 1, 3 or 12;
            // turning off its deflection, its attractor or LIMIT_ROLL_ONLY, or giving it angular friction, does not
            // change that, so it is in the contact solving, not in the vehicle model.
            //
            // The car is run from five starts a millimetre apart and the middle speed is read. Where the ramp begins the
            // heightfield has a seam, and whether the car catches on it (and runs 5% slow for the rest of the run) is decided by
            // millimetres: of 40 starts a millimetre apart, 11 caught at 11 Hz with the controller run once per step and 12
            // with it run in each collision step, and 3 and 3 at 22.5 Hz. A run from the one default start is a coin toss on
            // that seam, not a reading of the equation.
            var speeds = new List<double>();
            for (int start = 0; start < 5; start++)
            {
                var o = new HarnessOptions { RateHz = rate, SlopeDeg = slope, StartOffsetY = start * 0.001f, Jolt = { ["VehiclePresets"] = presets } }.SecondLife();
                if (rate > 45.0)
                    o.Jolt["CollisionSteps"] = "3";
                speeds.Add(Harness.Harness.Run(Harness.Harness.Find(scenario), o).Summary.SteadySpeed);
            }
            speeds.Sort();
            double steady = speeds[2];
            double expected = EquationSteady(rate, 8.0, tm, td, tf, slope, limitMotorUp: true);
            // The documented test car (LIMIT_ROLL_ONLY with no angular friction, so its pitch rests on the contact
            // alone): 1.5% for it.
            double tolerance = presets == "documented" ? 0.015 : 0.01;
            Assert.True(Math.Abs(steady - expected) <= tolerance * expected, $"{scenario} {slope} deg at {rate} Hz: {steady:0.000} against {expected:0.000}");
        }
    }
}
