/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vertical attractor as a damped spring on roll and pitch (timescale T, efficiency from wobbling 0 to
/// exponential decay 1), stepped exactly: a buoyant box tilted 30 degrees rights itself at the four rates; and the
/// test car over the 33 degree crest with its key held. Serial with the other native tests: real backends.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleAttractorTests
{
    private const double StartTilt = 30.0;

    private static RunResult Run(string scenario, double rate, float timescale, float efficiency, float duration)
    {
        var o = new HarnessOptions { RateHz = rate, Duration = duration }.SecondLife();
        o.VehicleParams.Add(VehicleParamSetting.Parse("VERTICAL_ATTRACTION_TIMESCALE=" + timescale.ToString(CultureInfo.InvariantCulture)));
        o.VehicleParams.Add(VehicleParamSetting.Parse("VERTICAL_ATTRACTION_EFFICIENCY=" + efficiency.ToString(CultureInfo.InvariantCulture)));
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    public static IEnumerable<object[]> AxesAndRates()
    {
        foreach (string s in new[] { "attract-roll", "attract-pitch" })
            foreach (double r in Harness.Harness.Rates)
                yield return new object[] { s, r };
    }

    // Critically damped, the tilt follows 30 e^(-t / T) (1 + t / T): within 0.5 degree all the way, and within 2% of
    // the start after 5.834 timescales.
    [Theory]
    [MemberData(nameof(AxesAndRates))]
    public void A_tilted_vehicle_rights_itself_in_the_documented_time(string scenario, double rate)
    {
        foreach (float ts in new[] { 1f, 2f })
        {
            RunResult r = Run(scenario, rate, ts, 1f, (float)Harness.Harness.AttractStart + 6f * ts + 2f);
            foreach (Sample s in r.Samples.Where(s => s.T > Harness.Harness.AttractStart))
            {
                double x = (s.T - Harness.Harness.AttractStart) / ts, expected = StartTilt * Math.Exp(-x) * (1 + x);
                Assert.True(Math.Abs(s.Tilt - expected) < 0.5, $"{scenario} T {ts} at {rate} Hz, {s.T:0.000} s: {s.Tilt:0.00} against {expected:0.00}");
            }
            Sample settled = r.Samples.First(s => s.T > Harness.Harness.AttractStart && s.Tilt < 0.02 * StartTilt);
            double at = Harness.Harness.AttractStart + 5.834 * ts;
            Assert.True(Math.Abs(settled.T - at) <= 0.03 * 5.834 * ts + 1.0 / rate, $"{scenario} T {ts} at {rate} Hz: within 2% at {settled.T:0.000} s against {at:0.000}");
        }
    }

    // Below critical damping the vehicle swings past up by e^(-pi z / sqrt(1 - z^2)) of its tilt: the efficiency
    // sets the overshoot, more efficiency less overshoot (0 swings back the whole way).
    [Theory]
    [MemberData(nameof(AxesAndRates))]
    public void The_efficiency_sets_the_overshoot(string scenario, double rate)
    {
        double last = double.MaxValue;
        foreach (float eff in new[] { 0f, 0.3f, 0.6f })
        {
            RunResult r = Run(scenario, rate, 1f, eff, (float)Harness.Harness.AttractStart + 8f);
            int through = r.Samples.FindIndex(s => s.T > Harness.Harness.AttractStart && s.Tilt < 1f);
            int nextUp = r.Samples.FindIndex(through, s => s.Tilt > 2f);
            int back = nextUp < 0 ? r.Samples.Count : r.Samples.FindIndex(nextUp, s => s.Tilt < 1f);
            if (back < 0) back = r.Samples.Count;
            double swing = (nextUp < 0 ? 0f : r.Samples.Skip(nextUp).Take(back - nextUp).Max(s => s.Tilt)) / StartTilt;
            double expected = Math.Exp(-Math.PI * eff / Math.Sqrt(1 - eff * eff));
            Assert.True(Math.Abs(swing - expected) < 0.03, $"{scenario} efficiency {eff} at {rate} Hz: overshoot {swing:0.000} against {expected:0.000}");
            Assert.True(swing < last);
            last = swing;
        }
    }

    // The test car off the 33 degree crest with its key held: in flight it is never faster than its speed at the
    // crest and the height it has fallen since allow (v^2 <= v0^2 + 2 g drop), and 11 and 45 Hz land alike, to within
    // 0.75 m: the landing is read at the first heartbeat that finds the car touching, and the car moves 0.36 m in an 11 Hz
    // heartbeat at the 4 m/s it lands at, so a heartbeat's difference in when the contact is reported is half of that
    // (run in each collision step, the car at 11 Hz is reported one heartbeat later than it was: 96.73 m against 97.05 m
    // before, and 97.36 m against 97.41 m at 45 Hz).
    // LIMIT_ROLL_ONLY is removed so the attractor holds the pitch: with it the pitch is free, the car leaves the
    // crest with the spin the crest contact gives it at each rate, and its linear friction along a nose pitched
    // differently lands it 96.61 m at 11 Hz and 97.24 m at 45 Hz.
    [Fact]
    public void Over_the_crest_the_car_gains_no_speed_but_what_gravity_gives()
    {
        var landings = new List<float>();
        foreach (double rate in new[] { 11.0, 45.0 })
        {
            var o = new HarnessOptions { RateHz = rate, SlopeDeg = 33f, Jolt = { ["VehiclePresets"] = "legacy" } }.SecondLife();
            o.VehicleFlags.Add(VehicleFlagSetting.Parse("-LIMIT_ROLL_ONLY"));
            RunResult r = Harness.Harness.Run(Harness.Harness.Find("testcar-down"), o);
            int off = r.Samples.FindIndex(s => !s.Touching && s.Position.Y < Course.RampTopY + 1f);
            Assert.True(off > 0, $"{rate} Hz: the car never left the ground at the crest");
            Sample crest = r.Samples[off - 1];
            int down = r.Samples.FindIndex(off, s => s.Touching);
            Assert.True(down > off + 2, $"{rate} Hz: no flight");
            for (int i = off; i < down; i++)
            {
                Sample s = r.Samples[i];
                double allowed = Math.Sqrt(crest.Speed * crest.Speed + 2 * 9.80665 * (crest.Position.Z - s.Position.Z));
                Assert.True(s.Speed <= allowed * 1.01 + 0.05, $"{rate} Hz, {s.T:0.000} s: {s.Speed:0.000} m/s, gravity allows {allowed:0.000}");
            }
            landings.Add(r.Samples[down].Position.Y);
        }
        Assert.True(Math.Abs(landings[0] - landings[1]) < 0.75f, $"lands at y {landings[0]:0.00} and {landings[1]:0.00}");
    }
}
