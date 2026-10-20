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
/// The vehicle's wind (InWorldz behaviour, VEHICLE_FLAG_REACT_TO_WIND) is read at the body's position in every collision step
/// and applied over that step's own length, as a change of velocity. At the controller level, on a body with no engine: the
/// drift speed is the same at 32, 45 and 64 Hz with 1 to 4 collision steps (within 1% of the wind's speed), and a wind that
/// changes between two collision steps, even of one Update, is followed by the very next step. Pure, so the class runs in
/// parallel with the others.
/// </summary>
public class VehicleWindRateTests
{
    private readonly ITestOutputHelper _out;
    public VehicleWindRateTests(ITestOutputHelper output) { _out = output; }

    private const float Wind = 5f;

    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private void Report(string test, double rate, int n, double t, double got, double expected)
    {
        string line = $"[rate-table] {test} | {rate} Hz x {n} | t {t:0.000} | {got:0.0000} against {expected:0.0000} | {Math.Abs(got - expected) / Wind * 100.0:0.00}% of {Wind}";
        _out.WriteLine(line);
    }

    private static BehaviourRig Rig(float efficiency)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz }, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessVehicleFlags(0x20000, false);   // VEHICLE_FLAG_REACT_TO_WIND, as a script numbers it
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(efficiency, efficiency, 0f));
        rig.Body.WindAt = _ => new Vector3(Wind, 0f, 0f);
        return rig;
    }

    // While the vehicle is slower than the wind the push is wind x efficiency, so v = 2.5 t for 5 m/s at 0.5.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_vehicle_in_a_constant_wind_gains_speed_alike_at_every_rate(double rate, int n)
    {
        var rig = Rig(0.5f);
        double h = 1.0 / rate;
        int steps1 = (int)Math.Round(rate), steps2 = (int)Math.Round(rate * 1.6);
        for (int i = 0; i < steps1; i++)
            rig.StepUpdate(h, n);
        Report("wind ramp", rate, n, 1.0, rig.Body.LinearVelocity.X, 2.5);
        Assert.InRange(rig.Body.LinearVelocity.X, 2.5 - 0.01 * Wind, 2.5 + 0.01 * Wind);
        for (int i = steps1; i < steps2; i++)
            rig.StepUpdate(h, n);
        Report("wind ramp", rate, n, 1.6, rig.Body.LinearVelocity.X, 4.0);
        Assert.InRange(rig.Body.LinearVelocity.X, 4.0 - 0.01 * Wind, 4.0 + 0.01 * Wind);
    }

    // The push stops once the vehicle moves the way the wind blows as fast as the wind: it settles at the wind's speed, past
    // it by at most the last step's push, which is the same at every rate to within the step (a gentle efficiency keeps that
    // inside 1% of the wind).
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_vehicle_in_a_constant_wind_settles_at_the_wind_speed_alike_at_every_rate(double rate, int n)
    {
        var rig = Rig(0.2f);
        double h = 1.0 / rate;
        int steps = (int)Math.Round(rate * 10.0);
        for (int i = 0; i < steps; i++)
            rig.StepUpdate(h, n);
        Report("wind settle", rate, n, 10.0, rig.Body.LinearVelocity.X, Wind);
        Assert.InRange(rig.Body.LinearVelocity.X, Wind * 0.99, Wind * 1.01);
    }

    // The wind flips direction between two collision steps: the next step already pushes with the new wind, not one Update or
    // eight Updates later. The change a step makes is exactly wind x efficiency x the step's length.
    [Theory]
    [InlineData(45.0, 1)]
    [InlineData(45.0, 2)]
    [InlineData(45.0, 3)]
    [InlineData(64.0, 4)]
    [InlineData(32.0, 4)]
    public void A_wind_that_flips_is_followed_within_one_collision_step(double rate, int n)
    {
        var rig = Rig(0.5f);
        float direction = 1f;
        rig.Body.WindAt = _ => new Vector3(Wind * direction, 0f, 0f);
        double h = 1.0 / rate, sub = h / n;

        // Two whole Updates at +5, then flip between the collision steps of the third Update, after its first step.
        rig.StepUpdate(h, n);
        rig.StepUpdate(h, n);
        rig.Car.BeginUpdate((float)h);
        float before = rig.Body.LinearVelocity.X;
        rig.Car.StepOnce((float)sub, rig.Body);
        float firstStep = rig.Body.LinearVelocity.X - before;
        rig.Body.Advance(sub);
        Assert.Equal(Wind * 0.5f * sub, firstStep, 4);   // pushed by the +5 wind

        direction = -1f;
        for (int k = 1; k < n; k++)
        {
            before = rig.Body.LinearVelocity.X;
            rig.Car.StepOnce((float)sub, rig.Body);
            float change = rig.Body.LinearVelocity.X - before;
            rig.Body.Advance(sub);
            Assert.Equal(-Wind * 0.5f * sub, change, 4);   // the very next collision step, in the same Update
        }
        if (n == 1)
        {
            // One collision step to an Update: the flip lands between two Updates, and the next Update's step takes it.
            rig.Car.BeginUpdate((float)h);
            before = rig.Body.LinearVelocity.X;
            rig.Car.StepOnce((float)sub, rig.Body);
            Assert.Equal(-Wind * 0.5f * sub, rig.Body.LinearVelocity.X - before, 4);
        }
    }

    // The sample is taken at the body's position of that step: a vehicle driving out of a gust into calm air is pushed by the
    // gust only while it is inside it, to the step.
    [Fact]
    public void The_wind_is_read_at_the_position_of_each_collision_step()
    {
        var rig = Rig(0.5f);
        rig.Body.WindAt = p => p.X < 130f ? new Vector3(Wind, 0f, 0f) : Vector3.Zero;
        rig.Body.Position = new Vector3(129.995f, 128f, 100f);
        rig.Body.LinearVelocity = new Vector3(2f, 0f, 0f);
        double sub = 1.0 / 45 / 4;
        rig.Car.BeginUpdate((float)(1.0 / 45));
        var pushed = new List<bool>();
        for (int i = 0; i < 4; i++)
        {
            float before = rig.Body.LinearVelocity.X;
            rig.Car.StepOnce((float)sub, rig.Body);
            pushed.Add(rig.Body.LinearVelocity.X > before + 1e-6f);
            rig.Body.Advance(sub);
        }
        // 2 m/s covers 0.011 m in a collision step: the first step starts 5 mm inside the gust and ends outside it, so the
        // steps after it get no push.
        Assert.Contains(true, pushed);
        Assert.Contains(false, pushed);
        Assert.True(pushed.IndexOf(false) > pushed.IndexOf(true));
    }
}
