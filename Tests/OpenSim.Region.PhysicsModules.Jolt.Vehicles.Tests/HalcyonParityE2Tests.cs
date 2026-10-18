/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Under <c>[Jolt] VehicleBehaviour = InWorldz</c> the module follows Halcyon's own laws for banking (its share of the angular
/// motor's sum and its turn curve) and for the vehicle hover force. Each test runs the module in a pure rig (a body with no
/// engine) at 45 Hz and at 11 Hz with six collision steps, against the translated model of Halcyon's law at Halcyon's own step of
/// 15 ms (HalcyonAttitudeModel, HalcyonHoverModel), on the scenario HalcyonParityETests measured, with the tolerance stated in
/// the test. Pure, parallel: no static state, no files, no engine.
/// </summary>
public class HalcyonParityE2Tests
{
    private readonly ITestOutputHelper _out;
    public HalcyonParityE2Tests(ITestOutputHelper output) => _out = output;

    private const double Deg = Math.PI / 180.0;
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static VehicleSettings InWorldz() => new() { Behaviour = VehicleBehaviour.InWorldz };

    // ------------------------------------------------------------------------------------------------ banking

    private static List<(double t, double w)> BankingHalcyon(double roll, float efficiency, float mix, float timescale, double duration)
    {
        var m = new HalcyonAttitudeModel
        {
            BankingEfficiency = efficiency, BankingMix = mix, BankingTimescale = timescale, HoldOrientation = true,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(roll * Deg)), ActorAngularDamping = 0f, Type = VehicleType.Airplane,
        };
        var trace = new List<(double, double)>();
        while (m.Time < duration - 1e-9)
        {
            m.Step(0.015f);
            trace.Add((m.Time, m.WorldAngularVel.Z));
        }
        return trace;
    }

    private static List<(double t, double w)> BankingModule(double rate, int collisionSteps, double roll, float efficiency, float mix, float timescale, double duration)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, efficiency);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, mix);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, timescale);
        rig.Body.HoldOrientation = true;
        rig.Tilt(roll, 0);
        var trace = new List<(double, double)>();
        double t = 0, step = 1.0 / rate;
        while (t < duration - 1e-9)
        {
            rig.StepUpdate(step, collisionSteps);
            t += step;
            trace.Add((t, rig.Body.AngularVelocity.Z));
        }
        return trace;
    }

    private static double At(List<(double t, double w)> trace, double t)
    {
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].t >= t - 1e-9)
            {
                double f = (t - trace[i - 1].t) / (trace[i].t - trace[i - 1].t);
                return trace[i - 1].w + f * (trace[i].w - trace[i - 1].w);
            }
        return trace[^1].w;
    }

    private static double TimeToShare(List<(double t, double w)> trace, double target, double share)
    {
        foreach (var s in trace)
            if (Math.Abs(s.w) >= share * Math.Abs(target)) return s.t;
        return double.NaN;
    }

    private static readonly double[] Times = { 0.5, 1, 2, 4, 8, 20 };

    [Fact]
    public void A_gentle_bank_is_held_to_Halcyons_dead_band_by_the_sum_it_thresholds()
    {
        // Roll 5 degrees, efficiency 0.2, timescale 1 s: target -0.0548 rad/s, dead band pi / 512 = 0.0061. Halcyon adds the
        // banking turn's change to the angular motor's and applies the sum only when it is at least the dead band, so its
        // turn stays at its first kick of 8 pi / 512 (-0.049). Tolerance: one dead band (0.0061) at every time.
        double band = Math.PI / 512;
        var a = BankingHalcyon(5.0, 0.2f, 0f, 1f, 20.0);
        var c45 = BankingModule(45, 1, 5.0, 0.2f, 0f, 1f, 20.0);
        var c11 = BankingModule(11, 6, 5.0, 0.2f, 0f, 1f, 20.0);
        _out.WriteLine("rate at 0.5, 1, 2, 4, 8, 20 s");
        foreach (var (name, trace) in new[] { ("Halcyon 15 ms", a), ("module 45 Hz", c45), ("module 11 Hz", c11) })
            _out.WriteLine($"{name,-14} " + string.Join(", ", Times.Select(t => F(At(trace, t)))));
        foreach (var (name, trace) in new[] { ("45 Hz", c45), ("11 Hz", c11) })
        {
            double worst = Times.Max(t => Math.Abs(At(trace, t) - At(a, t)));
            _out.WriteLine($"{name}: largest difference from Halcyon {F(worst)} rad/s (tolerance {F(band)})");
            Assert.True(worst <= band, $"{name}: the module's turn differs from Halcyon's by {worst} rad/s");
            Assert.InRange(Math.Abs(At(trace, 20.0)), 0.048, 0.050);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(3f)]
    public void The_banking_turn_builds_by_Halcyons_log_growth_curve(float timescale)
    {
        // Airplane rolled 30 degrees, efficiency 1, mix 0, held in attitude: target -1.571 rad/s. Tolerance: the time to 63% and
        // to 90% of the target within 10% of Halcyon's, and the rate within 0.1 rad/s at every time.
        double target = -Math.Sin(30 * Deg) * Math.PI;
        var a = BankingHalcyon(30.0, 1f, 0f, timescale, 20.0);
        var c45 = BankingModule(45, 1, 30.0, 1f, 0f, timescale, 20.0);
        var c11 = BankingModule(11, 6, 30.0, 1f, 0f, timescale, 20.0);
        foreach (var (name, trace) in new[] { ("Halcyon 15 ms", a), ("module 45 Hz", c45), ("module 11 Hz", c11) })
            _out.WriteLine($"{name,-14} 63% at {F(TimeToShare(trace, target, 0.632))} s, 90% at {F(TimeToShare(trace, target, 0.9))} s; rate at 0.5, 1, 2, 4, 8, 20 s: "
                + string.Join(", ", Times.Select(t => F(At(trace, t)))));
        foreach (var (name, trace) in new[] { ("45 Hz", c45), ("11 Hz", c11) })
        {
            foreach (double share in new[] { 0.632, 0.9 })
            {
                double ta = TimeToShare(a, target, share), tc = TimeToShare(trace, target, share);
                Assert.True(Math.Abs(tc - ta) <= 0.1 * ta, $"{name}: {share:P0} of the target at {tc} s against Halcyon's {ta} s");
            }
            double worst = Times.Max(t => Math.Abs(At(trace, t) - At(a, t)));
            Assert.True(worst <= 0.1, $"{name}: the rate differs from Halcyon's by {worst} rad/s");
        }
    }

    // ------------------------------------------------------------------------------------------------ hover

    private static List<(double t, double z)> HoverHalcyon(float tau, float eff)
    {
        var m = new HalcyonHoverModel { Height = 5f, Tau = tau, Damping = eff, Z = 8f };
        var trace = new List<(double, double)>();
        double t = 0;
        while (t < 20.0 - 1e-9) { m.Step(0.015f); t += 0.015; trace.Add((t, m.Z)); }
        return trace;
    }

    private static List<(double t, double z)> HoverModule(double rate, int collisionSteps, float tau, float eff)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, tau);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, eff);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Body.Position = new Vector3(128f, 128f, 8f);
        var trace = new List<(double, double)>();
        double t = 0, step = 1.0 / rate;
        while (t < 20.0 - 1e-9) { rig.StepUpdate(step, collisionSteps); t += step; trace.Add((t, rig.Body.Position.Z)); }
        return trace;
    }

    private static double ZAt(List<(double t, double z)> trace, double t)
    {
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].t >= t - 1e-9)
            {
                double f = (t - trace[i - 1].t) / (trace[i].t - trace[i - 1].t);
                return trace[i - 1].z + f * (trace[i].z - trace[i - 1].z);
            }
        return trace[^1].z;
    }

    [Theory]
    [InlineData(1f, 0.2f)]
    [InlineData(1f, 0.8f)]
    [InlineData(3f, 0.2f)]
    [InlineData(3f, 0.8f)]
    [InlineData(0.3f, 0.5f)]
    public void A_vehicle_released_3_m_above_its_hover_height_follows_Halcyons_hover(float tau, float eff)
    {
        // Released at 8 m over a 5 m hover height (global). Tolerance: the height within 0.25 m of Halcyon's at every sample of
        // the 20 s, and the lowest point within 0.15 m of Halcyon's.
        var a = HoverHalcyon(tau, eff);
        double aLowest = a.Min(s => s.z);
        foreach (var (name, rate, n) in new[] { ("45 Hz", 45.0, 1), ("11 Hz x 6", 11.0, 6) })
        {
            var c = HoverModule(rate, n, tau, eff);
            double worst = 0;
            for (double t = 0.1; t <= 19.9; t += 0.1)
                worst = Math.Max(worst, Math.Abs(ZAt(c, t) - ZAt(a, t)));
            double cLowest = c.Min(s => s.z);
            _out.WriteLine($"tau {tau}, efficiency {eff}, {name}: lowest {F(cLowest)} against Halcyon's {F(aLowest)}; largest height difference {F(worst)} m; z at 10 s {F(ZAt(c, 10))} against {F(ZAt(a, 10))}");
            Assert.True(worst <= 0.25, $"{name}: the height differs from Halcyon's by {worst} m");
            Assert.True(Math.Abs(cLowest - aLowest) <= 0.15, $"{name}: lowest point {cLowest} against Halcyon's {aLowest}");
        }
    }
}
