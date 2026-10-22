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
/// Hover as a damped spring on the height error (timescale T, efficiency from bouncy 0 to critically damped 1),
/// stepped exactly: the harness's hover scenario (a balloon with no vertical friction, lifted from the ground to its
/// hover height) at the four rates. Serial with the other native tests: real backends on the shared pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleHoverTests
{
    private const float Height = 5f;
    private const float StartHeight = 0.52f;   // the box's centre on the ground (AddBoxOnGround)

    private static RunResult Run(double rate, float timescale, float efficiency, float duration, float height = Height, params string[] flags)
    {
        var o = new HarnessOptions { RateHz = rate, Duration = duration }.SecondLife();
        o.VehicleParams.Add(VehicleParamSetting.Parse($"HOVER_TIMESCALE={timescale.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        o.VehicleParams.Add(VehicleParamSetting.Parse($"HOVER_EFFICIENCY={efficiency.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        o.VehicleParams.Add(VehicleParamSetting.Parse($"HOVER_HEIGHT={height.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        foreach (string f in flags)
            o.VehicleFlags.Add(VehicleFlagSetting.Parse(f));
        return Harness.Harness.Run(Harness.Harness.Find("hover"), o);
    }

    [Fact]
    public void The_spring_step_is_exact_however_the_time_is_cut()
    {
        foreach (double eff in new[] { 0.0, 0.3, 0.8, 1.0 })
        {
            (double e1, double v1) = VehicleSpring.Step(-4.0, 0.5, 2.0, eff, 1.0);
            foreach (int n in new[] { 11, 45, 90 })
            {
                double e = -4.0, v = 0.5;
                for (int i = 0; i < n; i++)
                    (e, v) = VehicleSpring.Step(e, v, 2.0, eff, 1.0 / n);
                Assert.True(Math.Abs(e - e1) < 1e-9 && Math.Abs(v - v1) < 1e-9, $"eff {eff}, {n} steps: {e}, {v} against {e1}, {v1}");
            }
        }
        // Critically damped from rest: e^(-t / T) (1 + t / T) of the error.
        (double ec, _) = VehicleSpring.Step(1.0, 0.0, 2.0, 1.0, 2.0);
        Assert.Equal(Math.Exp(-1) * 2, ec, 9);
    }

    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void It_settles_on_its_hover_height(double rate)
    {
        Summary m = Run(rate, 2f, 1f, 30f).Summary;
        Assert.InRange(m.End.Z - Course.Ground, Height - 0.02f, Height + 0.02f);
        Assert.InRange(m.PeakHeight, Height - 0.02f, Height + 0.02f);   // critically damped: no overshoot
    }

    // The overshoot of a spring from rest with damping ratio z: e^(-pi z / sqrt(1 - z^2)) of the start's error. A
    // higher efficiency overshoots less (the documented slider from bouncy to critically damped).
    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void The_overshoot_follows_the_efficiency(double rate)
    {
        float last = float.MaxValue;
        foreach (float eff in new[] { 0f, 0.2f, 0.5f, 0.8f })
        {
            Summary m = Run(rate, 2f, eff, 20f).Summary;
            float overshoot = (m.PeakHeight - Height) / (Height - StartHeight);
            double expected = Math.Exp(-Math.PI * eff / Math.Sqrt(1 - eff * eff));
            Assert.True(Math.Abs(overshoot - expected) < 0.02, $"efficiency {eff} at {rate} Hz: overshoot {overshoot:0.000} against {expected:0.000}");
            Assert.True(overshoot < last, $"efficiency {eff}: overshoot {overshoot} not below {last}");
            last = overshoot;
        }
    }

    // Critically damped, the error is within 2% of the start's after 5.834 timescales (e^(-x) (1 + x) = 0.02).
    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void The_settling_time_follows_the_timescale(double rate)
    {
        foreach (float ts in new[] { 1f, 2f, 4f })
        {
            RunResult r = Run(rate, ts, 1f, 6f * ts + 4f);
            float band = 0.02f * (Height - StartHeight);
            Sample first = r.Samples.First(s => Math.Abs(s.Height - Height) < band);
            // The type is set with the region running, which holds nothing still (the hold of the first
            // ReassertVehicleSeconds is for a vehicle made while the region loads), so the spring starts at once.
            double expected = 5.834 * ts;
            Assert.True(Math.Abs(first.T - expected) <= 0.02 * expected + 1.0 / rate, $"timescale {ts} at {rate} Hz: settled at {first.T:0.000} s against {expected:0.000}");
        }
    }

    // The base height: terrain and water by default (the higher; the course's water, 20 m, lies under its ground,
    // 25 m), water or terrain alone with their flags, or the global height.
    [Theory]
    [InlineData(11.0, 8f, Course.Ground + 8f)]
    [InlineData(11.0, 8f, Course.Ground + 8f, "HOVER_TERRAIN_ONLY")]
    [InlineData(11.0, 8f, Course.Water + 8f, "HOVER_WATER_ONLY")]
    [InlineData(11.0, 40f, 40f, "HOVER_GLOBAL_HEIGHT")]
    [InlineData(45.0, 8f, Course.Ground + 8f)]
    [InlineData(45.0, 8f, Course.Ground + 8f, "HOVER_TERRAIN_ONLY")]
    [InlineData(45.0, 8f, Course.Water + 8f, "HOVER_WATER_ONLY")]
    [InlineData(45.0, 40f, 40f, "HOVER_GLOBAL_HEIGHT")]
    public void The_hover_flags_choose_the_height_it_hovers_over(double rate, float height, float expectedZ, params string[] flags)
    {
        Summary m = Run(rate, 1f, 1f, 15f, height, flags).Summary;
        Assert.InRange(m.End.Z, expectedZ - 0.02f, expectedZ + 0.02f);
    }

    // HOVER_GLOBAL_HEIGHT with another hover flag: the global height wins, as Second Life's page for the flag documents
    // (the flag hovers at the global height rather than above ground or water) and as InWorldz's hover does (a global
    // hover overrides water and ground). Each case also sets the other flag; the course's ground is at 25 m and its
    // water at 20 m, so a vehicle that wrongly used either would settle there plus the height, not at 40 m.
    [Theory]
    [InlineData(11.0, "HOVER_GLOBAL_HEIGHT", "HOVER_WATER_ONLY")]
    [InlineData(11.0, "HOVER_GLOBAL_HEIGHT", "HOVER_TERRAIN_ONLY")]
    [InlineData(11.0, "HOVER_GLOBAL_HEIGHT", "HOVER_WATER_ONLY", "HOVER_TERRAIN_ONLY")]
    [InlineData(45.0, "HOVER_GLOBAL_HEIGHT", "HOVER_WATER_ONLY")]
    [InlineData(45.0, "HOVER_GLOBAL_HEIGHT", "HOVER_TERRAIN_ONLY")]
    [InlineData(45.0, "HOVER_GLOBAL_HEIGHT", "HOVER_WATER_ONLY", "HOVER_TERRAIN_ONLY")]
    public void Global_height_wins_over_the_other_hover_flags(double rate, params string[] flags)
    {
        Summary m = Run(rate, 1f, 1f, 15f, 40f, flags).Summary;
        Assert.InRange(m.End.Z, 40f - 0.02f, 40f + 0.02f);
    }

    // HOVER_WATER_ONLY and HOVER_TERRAIN_ONLY both set, the global flag clear: the vehicle hovers over both, that is over
    // the higher of the water and the ground, as with neither flag. Second Life's pages call the case undefined;
    // InWorldz's hover takes "both flags set or both clear" to mean both (VehicleDynamics.cs:1580-1588). The course's
    // ground is at 25 m and its water at 20 m, so over the water alone the vehicle would settle at 28 m, not 33 m.
    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void Water_only_and_terrain_only_together_hover_over_both(double rate)
    {
        Summary both = Run(rate, 1f, 1f, 15f, 8f, "HOVER_WATER_ONLY", "HOVER_TERRAIN_ONLY").Summary;
        Summary neither = Run(rate, 1f, 1f, 15f, 8f).Summary;
        Assert.InRange(both.End.Z, Course.Ground + 8f - 0.02f, Course.Ground + 8f + 0.02f);
        Assert.Equal(neither.End.Z, both.End.Z, 3);
    }

    // HOVER_UP_ONLY: hover does not push down, and buoyancy vanishes above the hover height. A bouncy hover that would
    // swing the balloon 4.5 m over its height instead lets it coast up under gravity alone from where it crosses the
    // height: v^2 / 2g above it at most, then down again.
    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void Up_only_hover_never_pushes_down(double rate)
    {
        Summary free = Run(rate, 2f, 0f, 20f).Summary;
        RunResult r = Run(rate, 2f, 0f, 20f, Height, "HOVER_UP_ONLY");
        Assert.InRange(free.PeakHeight, Height + 4f, Height + 5f);
        // The spring's speed through its height, from rest 4.48 m below: 4.48 m / T.
        double crossing = (Height - StartHeight) / 2.0;
        double coast = crossing * crossing / (2 * 9.80665);
        // The first rise over the height (until it is back under it). Gravity comes back from the first whole step
        // above the height: up to half a step of it late.
        double late = 0.5 * 9.80665 / (rate * rate);
        int over = r.Samples.FindIndex(s => s.Height > Height);
        int back = r.Samples.FindIndex(over, s => s.Height < Height);
        float firstPeak = r.Samples.Skip(over).Take(back - over).Max(s => s.Height);
        Assert.InRange(firstPeak, Height, Height + coast * 1.05 + late + 0.01);
        Assert.Contains(r.Samples, s => s.Height > Height + 0.5 * coast);   // it does go above: nothing pulls it down early
    }

    // A bouncy up-only hover (efficiency 0: an undamped spring below the height, a free coast above it) bounces back
    // to the same height every time: no later bounce rises over the first (within 1 cm), at every rate. The step
    // that falls back through the height lets the spring act from the moment it passes the height.
    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_bouncy_up_only_hover_gains_no_height_on_later_bounces(double rate)
    {
        RunResult r = Run(rate, 2f, 0f, 40f, Height, "HOVER_UP_ONLY");
        var peaks = new List<float>();
        int i = 0;
        while (true)
        {
            int over = r.Samples.FindIndex(i, s => s.Height > Height);
            if (over < 0) break;
            int back = r.Samples.FindIndex(over, s => s.Height < Height);
            if (back < 0) break;
            peaks.Add(r.Samples.Skip(over).Take(back - over).Max(s => s.Height));
            i = back;
        }
        Assert.True(peaks.Count >= 4, $"{rate} Hz: {peaks.Count} bounces");
        foreach (float p in peaks.Skip(1))
            Assert.True(p <= peaks[0] + 0.01f, $"{rate} Hz: bounces {string.Join(", ", peaks.Select(x => x.ToString("0.000")))}");
    }
}
