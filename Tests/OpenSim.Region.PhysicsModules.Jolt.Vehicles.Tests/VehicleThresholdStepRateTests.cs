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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The InWorldz Halcyon thresholds that are written for one 15 ms step (VehicleDynamics.cs:71 and :62: a change of angular
/// velocity under pi / 512 is not applied, and one under 0.001 is zeroed before it is handed to the body) are held as an
/// acceleration: a collision step of h seconds takes h / 15 ms of each, whatever the Update rate and the number of
/// collision steps. Each case is run at 32, 45 and 64 Hz Updates with 1 to 4 collision steps
/// (<see cref="BehaviourRig.StepUpdate"/>). A push a fifth under the line is dropped in every cell and one a fifth over it
/// is applied in every cell. Pure (a body with no engine), so the class runs in parallel.
/// </summary>
public class VehicleThresholdStepRateTests
{
    private readonly ITestOutputHelper _out;
    public VehicleThresholdStepRateTests(ITestOutputHelper output) { _out = output; }

    private const double HalcyonStep = 0.015;
    private const double AngularLine = Math.PI / 512.0;
    private const double ForceLine = 0.001;
    private static VehicleSettings InWorldz => new() { Behaviour = VehicleBehaviour.InWorldz };

    public static IEnumerable<object[]> Grid()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    // ---- angular deflection: a push under pi / 512 in 15 ms is dropped ---------------------------------------------

    // A car held at its attitude with a velocity 45 degrees off its nose; the efficiency is set so that the largest component
    // of one 15 ms push is 'times' of the line. The push grows the angular velocity by (push / 15 ms) each second it is applied.
    private static Vector3 DeflectedSpin(double times, double rate, int n, out double perSecond)
    {
        var velocity = new Vector3(20f, 20f, 0f);
        float reference = new VehicleSettings().ReferenceSpeed;
        Vector3 one = HalcyonVehicleLaws.AngularDeflectionPush(Quaternion.Identity, velocity, velocity, Vector3.Zero, 1f, 1f, (float)HalcyonStep, reference);
        double largest = Math.Max(Math.Abs(one.X), Math.Max(Math.Abs(one.Y), Math.Abs(one.Z)));
        double efficiency = times * AngularLine / largest;
        Assert.InRange(efficiency, 0.01, 1.0);

        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, (float)efficiency);
        rig.Body.HoldOrientation = true;
        rig.Body.LinearVelocity = velocity;
        int updates = (int)Math.Round(rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        perSecond = times * AngularLine / HalcyonStep;
        return rig.Body.AngularVelocity;
    }

    private static double Largest(Vector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_angular_deflection_push_under_pi_over_512_in_15_ms_is_dropped_at_every_rate(double rate, int n)
    {
        Vector3 spin = DeflectedSpin(0.8, rate, n, out double wouldBe);
        _out.WriteLine($"[thr] deflection under the line | {rate} Hz x {n} | spin {Largest(spin):0.00000} (applied it would be {wouldBe:0.00000})");
        Assert.True(Largest(spin) < 0.01, $"{rate} Hz x {n}: a push 0.8 of the line was applied: spin {Largest(spin):0.00000} rad/s");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_angular_deflection_push_over_pi_over_512_in_15_ms_is_applied_at_every_rate(double rate, int n)
    {
        Vector3 spin = DeflectedSpin(1.2, rate, n, out double expected);
        _out.WriteLine($"[thr] deflection over the line | {rate} Hz x {n} | spin {Largest(spin):0.00000} against {expected:0.00000}");
        Assert.InRange(Largest(spin), expected * 0.95, expected * 1.05);
    }

    // ---- the angular velocity change handed to the body: under 0.001 in 15 ms is zeroed ----------------------------------

    // A boat in a wind along its nose, the wind's angular efficiency set so that the pitch push is 'times' of the line a
    // second: 0.001 in 15 ms is 0.0667 rad/s^2.
    private static double WindPitch(double times, double rate, int n)
    {
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_BOAT, gravity: false);
        rig.Body.WindAt = _ => new Vector3(10f, 0f, 0f);
        rig.Body.HoldOrientation = true;
        rig.Car.ProcessVehicleFlags(0x20000, remove: false);   // REACT_TO_WIND
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, Vector3.Zero);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, new Vector3(0f, (float)(times * ForceLine / HalcyonStep / 10.0), 0f));
        int updates = (int)Math.Round(rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        return rig.Body.AngularVelocity.Y;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_angular_velocity_change_under_0001_in_15_ms_is_zeroed_at_every_rate(double rate, int n)
    {
        double pitch = WindPitch(0.8, rate, n);
        _out.WriteLine($"[thr] angular change under the line | {rate} Hz x {n} | pitch rate {pitch:0.00000}");
        Assert.True(Math.Abs(pitch) < 0.005, $"{rate} Hz x {n}: a change 0.8 of the line was applied: pitch rate {pitch:0.00000} rad/s");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_angular_velocity_change_over_0001_in_15_ms_is_applied_at_every_rate(double rate, int n)
    {
        double pitch = WindPitch(1.2, rate, n);
        double expected = 1.2 * ForceLine / HalcyonStep;
        _out.WriteLine($"[thr] angular change over the line | {rate} Hz x {n} | pitch rate {pitch:0.00000} against {expected:0.00000}");
        Assert.InRange(pitch, expected * 0.95, expected * 1.05);
    }
}
