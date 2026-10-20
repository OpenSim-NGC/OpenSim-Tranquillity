/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle's ground check (a prim's IsColliding, set from the solver's contacts each vehicle step) and the
/// [Jolt] VehicleGroundGravityFactor it gates. Serial with the other native tests: real backends on the shared pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleGroundTests
{
    private static RunResult Run(string scenario, float slope, double rate = 11.0, string groundGravity = null, float? hold = null)
    {
        var o = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Hold = hold }.SecondLife();
        if (groundGravity != null)
            o.Jolt["VehicleGroundGravityFactor"] = groundGravity;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_car_on_the_ground_reports_contact_and_an_airplane_in_the_air_does_not(double rate)
    {
        RunResult car = Run("car", 0f, rate);
        Assert.All(car.Samples.Where(s => s.T >= 0.5), s => Assert.True(s.Touching, $"car at {s.T:0.000} s, height {s.Height:0.000}"));

        // The airplane preset starts 40 m up and comes down to land within the run (its motor does not hold it up).
        RunResult plane = Run("airplane", 0f, rate);
        List<Sample> aloft = plane.Samples.Where(s => s.Height > 1.0f).ToList();
        Assert.NotEmpty(aloft);
        Assert.All(aloft, s => Assert.False(s.Touching, $"airplane at {s.T:0.000} s, {s.Height:0.000} m up"));
        Assert.Contains(plane.Samples, s => s.Touching && s.Height < 0.5f);
    }

    [Fact]
    public void A_car_off_the_ground_reports_no_contact()
    {
        // The car preset leaves the 33 degree ramp at its crest and flies: no contact while its centre is well clear
        // of the ground, contact again once it is back on it.
        RunResult r = Run("car", 33f);
        // Clear of the crest edge too: the check reports contact in the last step, and the car's centre is 1.5 m up
        // one step after it leaves the edge.
        List<Sample> flying = r.Samples.Where(s => s.Height > 1.6f).ToList();
        Assert.NotEmpty(flying);
        Assert.All(flying, s => Assert.False(s.Touching, $"at {s.T:0.000} s, {s.Height:0.000} m up"));
        Assert.Contains(r.Samples, s => s.Touching && s.T > flying[^1].T);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(15f)]
    public void With_the_default_factor_gravity_is_whole(float slope)
    {
        // The default (no key) and an explicit 1 give the same run, bit for bit.
        RunResult byDefault = Run("testcar", slope);
        RunResult whole = Run("testcar", slope, groundGravity: "1");
        Assert.Equal(byDefault.ToCsv(), whole.ToCsv());
    }

    // The test car's steady roll down a ramp, nose down the slope, with its friction timescale of 1 s along its own
    // forward axis: f * g * sin(angle) * 1 s, with f the share of gravity on the ground. Gravity is inside the
    // friction's equation, so the stepped roll settles there at every rate. (With gravity applied after friction's
    // step it settled at f g sin(angle) dt / (1 - e^(-dt / 1 s)): 4.5% above at 11 Hz, 1.1% at 45.)
    private static float SteadyRoll(float factor, float slope, double rate)
    {
        double a = slope * Math.PI / 180.0;
        return (float)(factor * 9.80665 * Math.Sin(a));
    }

    [Theory]
    [InlineData(5f, 11.0)]
    [InlineData(5f, 45.0)]
    [InlineData(15f, 11.0)]
    [InlineData(15f, 45.0)]
    public void A_factor_of_a_fifth_cuts_the_steady_roll_as_the_formula_gives(float slope, double rate)
    {
        // The key is held for 15 s, which brings the car under the motor's steady speed (about 3.7 m/s) down to the
        // measured stretch of the ramp (y 45 to 70) before it is let go, so the slow roll gets there within the run.
        float whole = Run("testcar", slope, rate, hold: 15f).Summary.SteadySpeed;
        float fifth = Run("testcar", slope, rate, groundGravity: "0.2", hold: 15f).Summary.SteadySpeed;
        // In proportion to the factor ...
        Assert.InRange(fifth / whole, 0.19f, 0.215f);
        // ... and near the formula (the harness car rolls up to a few percent faster than it: it is a box, not a point).
        Assert.InRange(fifth, SteadyRoll(0.2f, slope, rate) * 0.97f, SteadyRoll(0.2f, slope, rate) * 1.10f);
        Assert.InRange(whole, SteadyRoll(1f, slope, rate) * 0.97f, SteadyRoll(1f, slope, rate) * 1.10f);
    }
}
