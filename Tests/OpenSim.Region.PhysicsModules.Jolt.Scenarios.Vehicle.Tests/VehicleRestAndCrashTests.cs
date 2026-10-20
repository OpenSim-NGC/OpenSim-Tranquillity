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
/// Parked vehicles sleep, and vehicles that crash stay physical: the harness's park-* and crash-* scenarios at 11 and
/// 45 Hz. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleRestAndCrashTests
{
    private static RunResult Run(string scenario, double rate, string presets = null)
    {
        var o = new HarnessOptions { RateHz = rate }.SecondLife();
        if (presets != null)
            o.Jolt["VehiclePresets"] = presets;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    // A parked vehicle goes to sleep within this many seconds of its last motor input (park-new: of the start) and
    // stays asleep to the end of the run; the engine then has no body awake.
    [Theory]
    [InlineData("park-new", 11.0, 6.0, null)]
    [InlineData("park-new", 45.0, 6.0, null)]
    [InlineData("park-faded", 11.0, 14.0, null)]
    [InlineData("park-faded", 45.0, 14.0, null)]
    [InlineData("park-drive", 11.0, 14.0, null)]
    // At 45 Hz the test car comes to rest tilted 0.2 to 0.3 degrees (legacy: angular deflection pitches it toward the
    // jitter of its contact velocity; documented: rolled, with nothing to level it). With no contact friction the
    // ground's push along that tilt slid it at 0.02 to 0.035 m/s until 20 to 26 s after the key; the rest rule
    // ([Jolt] VehicleRestSpeed) now holds it, its steady speed there being about 0.05 m/s.
    [InlineData("park-drive", 45.0, 10.0, "legacy")]
    [InlineData("park-drive", 45.0, 10.0, "documented")]
    public void A_parked_vehicle_goes_to_sleep_and_stays_asleep(string scenario, double rate, double within, string presets)
    {
        RunResult r = Run(scenario, rate, presets);
        Summary m = r.Summary;
        Assert.Equal(0, m.NonFinite);
        Assert.False(double.IsNaN(m.SleepAfter), $"{r.Name}: still awake at the end");
        Assert.InRange(m.SleepAfter, 0.0, within);
        Assert.Equal(0, m.ActiveAtEnd);
        // Asleep for at least the last 5 s of the run.
        Assert.All(r.Samples.Where(s => s.T >= r.Samples[^1].T - 5.0), s => Assert.Equal(0, s.Active));
    }

    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_sleeping_vehicle_wakes_when_its_motor_is_set(double rate)
    {
        RunResult r = Run("park-wake", rate);
        // Asleep for the two seconds before the key.
        Assert.All(r.Samples.Where(s => s.T >= Harness.Harness.WakeKeyAt - 2.0 && s.T < Harness.Harness.WakeKeyAt), s => Assert.Equal(0, s.Active));
        // The key drives it. The car preset's motor (timescale 1 s) against the ground's friction: wood (0.6) on land (0.4) combines
        // to 0.5, a deceleration of 0.5 g = 4.9 m/s2 while it moves, so dv/dt = (8 - v) / 1 - 4.9 and v = 3.1 (1 - e^-t), which
        // covers 3.1 (2 - (1 - e^-2)) = 3.5 m in the 2 s of the key (it was 9 to 15 m with the vehicle's friction at 0).
        Assert.InRange(r.Summary.DistanceBeforeRelease, 3f, 4f);
    }

    // A crash: every speed stays finite, nothing passes through what it hit or sinks deep into it, and no object
    // leaves the impact faster than the fastest one arrived, by more than LeavingMargin.
    private const float LeavingMargin = 0.05f;
    private const float MaxPenetration = 0.5f;

    [Theory]
    [InlineData("crash-wall", 11.0)]
    [InlineData("crash-wall", 45.0)]
    [InlineData("crash-box", 11.0)]
    [InlineData("crash-box", 45.0)]
    [InlineData("crash-headon", 11.0)]
    [InlineData("crash-headon", 45.0)]
    [InlineData("crash-drop", 11.0)]
    [InlineData("crash-drop", 45.0)]
    public void A_crash_stays_finite_and_adds_no_speed(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate);
        Summary m = r.Summary;
        Assert.Equal(0, m.NonFinite);
        Assert.False(double.IsNaN(m.ImpactT), $"{r.Name}: no impact");
        Assert.Equal(0, m.Tunneled);
        Assert.InRange(m.Penetration, 0f, MaxPenetration);
        Assert.InRange(m.LeavingSpeed, 0f, m.ArrivalSpeed * (1f + LeavingMargin));
    }

    // Small changes to each crash (arrival speed 0.9 and 1.1 of the scenario's, the car 0.2 m to either side, turned
    // 1 degree either way) at 11 and 45 Hz: no outcome is violent. Each stays finite, nothing passes through what it
    // hit, nothing is pushed into the other deeper than a quarter of a metre (half the car's height: deeper, the
    // shortest way out can be through the other side), and nothing leaves faster than both the fastest arrival and
    // what the held key's motor drives the car to on its own (20 m/s x the speed share x Tf / (Tf + Tm), the car
    // preset's 100 s forward friction and 1 s motor timescale), by more than LeavingMargin: a car that glances off and
    // still has its key down speeds up again, a contact that throws it does not count on that.
    public static IEnumerable<object[]> CrashVariants()
    {
        foreach (string scenario in new[] { "crash-wall", "crash-box", "crash-headon", "crash-drop" })
            foreach (double rate in new[] { 11.0, 45.0 })
                yield return new object[] { scenario, rate };
    }

    [Theory]
    [MemberData(nameof(CrashVariants))]
    public void Small_changes_to_a_crash_give_no_violent_outcome(string scenario, double rate)
    {
        foreach (float speed in new[] { 0.9f, 1.1f })
            foreach (float offset in new[] { -0.2f, 0.2f })
                foreach (float angle in new[] { -1f, 1f })
                {
                    var o = new HarnessOptions { RateHz = rate, CrashSpeed = speed, CrashOffset = offset, CrashAngle = angle }.SecondLife();
                    RunResult r = Harness.Harness.Run(Harness.Harness.Find(scenario), o);
                    Summary m = r.Summary;
                    Assert.Equal(0, m.NonFinite);
                    Assert.False(double.IsNaN(m.ImpactT), $"{r.Name}: no impact");
                    Assert.Equal(0, m.Tunneled);
                    Assert.True(m.Penetration <= 0.25f, $"{r.Name}: {m.Penetration:0.000} m into the other");
                    float motor = scenario == "crash-drop" ? 0f : 20f * speed * 100f / 101f;
                    float limit = MathF.Max(m.ArrivalSpeed, motor) * (1f + LeavingMargin);
                    Assert.True(m.LeavingSpeed <= limit, $"{r.Name}: left at {m.LeavingSpeed:0.000} m/s, arrived at {m.ArrivalSpeed:0.000}");
                }
    }

    // The two rates tell the same story: the impact at the same time (within one and a half 11 Hz samples, the
    // impact being seen in the first sample after it), the same speed leaving it (within 2 m/s or a
    // tenth of the arrival), and the objects in the same place 3 s later (within 2 m, or a tenth of the way the
    // car covered when it pushes a box ahead of it).
    [Theory]
    [InlineData("crash-wall")]
    [InlineData("crash-box")]
    [InlineData("crash-headon")]
    [InlineData("crash-drop")]
    public void A_crash_comes_out_alike_at_11_and_45_hz(string scenario)
    {
        // Two cars driven at each other bounce apart (a car keeps its material's restitution), meet again with their keys still
        // down and, at 11 Hz, glance and spin away where at 45 Hz they stop: after the second meeting the outcome is not a function of
        // the step rate. The head-on crash keeps the earlier material keys, in which a car does not bounce.
        MaterialPins.Enabled = scenario == "crash-headon";
        RunResult a = Run(scenario, 11.0), b = Run(scenario, 45.0);
        Assert.InRange(Math.Abs(a.Summary.ImpactT - b.Summary.ImpactT), 0.0, 1.5 / 11.0);
        float leaving = Math.Abs(a.Summary.LeavingSpeed - b.Summary.LeavingSpeed);
        Assert.True(leaving <= Math.Max(2f, 0.1f * a.Summary.ArrivalSpeed), $"leaving {a.Summary.LeavingSpeed:0.000} vs {b.Summary.LeavingSpeed:0.000}");
        Sample sa = At(a, a.Summary.ImpactT + 3.0), sb = At(b, b.Summary.ImpactT + 3.0);
        float covered = Math.Abs(sa.Position.X - a.Samples[0].Position.X);
        float apart = (sa.Position - sb.Position).Length();
        Assert.True(apart <= Math.Max(2f, 0.1f * covered), $"3 s after the impact: {sa.Position} vs {sb.Position}");
    }

    private static Sample At(RunResult r, double t) => r.Samples.First(s => s.T >= t - 1e-9);

    // Held against a wall with the key down for 45 s: the car never sinks more than 3 cm into the wall (the engine's
    // contact allowance being 2 cm) or passes through it, moves no more than 3 cm along the push from the start of
    // the push (2 s after the impact, when the impact is over) to the end, and in the last third of the push is still
    // (its range along the push 5 mm at most). After the impact the contact pushes the car back out of the wall
    // until it settles: on Windows in one steady movement over about 9 s, on Linux in bursts over about 30 s.
    // Measured (stock native, at velocity steps 20; deepest / net movement / last third's range):
    //   11 Hz: Windows 19.9 / 9.4 / 0.0 mm, Linux 20.0 / 17.6 / 1.7 mm;
    //   45 Hz: Windows 11.6 / 0.0 / 0.0 mm, Linux 11.5 / 0.0 / 0.0 mm.
    private const float PushSeconds = 45f;

    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_car_pushing_on_a_wall_stays_put(double rate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("crash-wall"), new HarnessOptions { RateHz = rate, Duration = PushSeconds }.SecondLife());
        Summary m = r.Summary;
        Assert.False(double.IsNaN(m.ImpactT), $"{r.Name}: no impact");
        double end = r.Samples[^1].T, from = m.ImpactT + 2.0;
        Assert.True(end >= PushSeconds - 1e-6, $"{r.Name}: the run ends at {end:0.00} s");
        Assert.Equal(0, m.Tunneled);
        Assert.True(m.Penetration <= 0.03f, $"{r.Name}: {m.Penetration * 1000f:0.0} mm into the wall");
        List<Sample> push = r.Samples.Where(s => s.T >= from - 1e-9).ToList();
        float moved = Math.Abs(push[^1].Position.X - push[0].Position.X);
        Assert.True(moved <= 0.03f, $"{r.Name}: moved {moved * 1000f:0.0} mm from the start of the push to the end");
        double lastThird = from + (end - from) * 2.0 / 3.0;
        List<float> late = push.Where(s => s.T >= lastThird - 1e-9).Select(s => s.Position.X).ToList();
        float range = late.Max() - late.Min();
        Assert.True(range <= 0.005f, $"{r.Name}: moved over {range * 1000f:0.0} mm in the last third of the push");
    }
}
