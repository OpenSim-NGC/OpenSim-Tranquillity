/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Does mouselook steering hunt? A car driven straight with its driver's camera held on the heading, the camera shaking
/// as a hand on a mouse does (a random yaw and pitch, a new one at every camera update), and a car turned through 90
/// degrees by the camera. On the real engine, at the default 45 Hz physics steps and at one step per heartbeat (11 Hz).
/// The figures: the wobble of the car's heading (its standard deviation, in degrees, after the first 5 s), against the
/// camera's own shake, and the overshoot and the settling of the 90 degree turn. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleMouselookHuntingTests
{
    private readonly ITestOutputHelper _out;
    public VehicleMouselookHuntingTests(ITestOutputHelper output) => _out = output;

    private const double Rad = 180.0 / Math.PI;

    private static RunResult Run(string scenario, double physicsRate, float noiseDegrees, double cameraRate, Action<HarnessOptions> more = null)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, CameraNoiseDegrees = noiseDegrees, CameraRateHz = cameraRate }.SecondLife();
        more?.Invoke(o);
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    private static double HeadingDegrees(Sample s) => Harness.Harness.Heading(s.Rotation) * Rad;

    private static (double std, double peak, double mean) Wobble(IEnumerable<double> headings)
    {
        double[] h = headings.ToArray();
        double mean = h.Average();
        double std = Math.Sqrt(h.Select(x => (x - mean) * (x - mean)).Average());
        return (std, h.Max(x => Math.Abs(x - mean)), mean);
    }

    // The camera held on the heading: the car goes straight, whatever the step rate.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_camera_held_on_the_heading_leaves_the_car_straight(double physicsRate)
    {
        RunResult r = Run("mouselook-hold", physicsRate, 0f, 30.0);
        double worst = r.Samples.Where(s => s.T >= 5.0).Max(s => Math.Abs(HeadingDegrees(s)));
        _out.WriteLine($"physics {physicsRate} Hz, camera held: largest heading error after 5 s {worst:0.0000} degrees over {r.Samples.Count} samples");
        // The heading is where the car's angular motor let go of it: the car lands with a small turn (about 0.002 rad/s) that the camera's
        // steering takes out over its first 3 s, and what is left when the motor's grip has decayed stays. That is a tenth of a degree
        // either way (0.01 to 0.04 degrees with the controller run once per step; 0.06 to 0.12 over eight starts a millimetre apart run in
        // each collision step, which takes the turn out a little slower), far inside the 2.8 degree dead zone.
        Assert.True(worst < 0.15, $"heading error {worst}");
    }

    // The shaking camera, 0.2, 0.5 and 1 degree. At 45 Hz physics steps the camera is read at every step, so it shakes
    // at 15, 30 and 60 updates a second as asked; with one step per heartbeat (11 Hz) it is read once per heartbeat and
    // the rate does not matter, so one is run. The wobble of the heading stays under the shake itself (the steering
    // filters it rather than amplifying it), the largest excursion stays within 1.5 times the shake, and the wobble of
    // each 5 s window of the run stays small beside it (it does not grow). The car must also not steer to one side: the
    // mean heading, taken over the runs of eight camera seeds. One run's mean is a finite sample of a heading that also
    // wanders slowly of itself (0.06 degrees, standard deviation, whatever the shake: 40 seeds at 0.2 and 1 degree, on
    // Windows and Linux, give means that centre on 0 with that spread), so a single run's mean falls outside 0.3 of a
    // 0.2 degree shake about one time in three on either platform. The mean of eight runs has a spread of 0.06 over
    // the square root of 8, about 0.02 degrees, and the limit of 0.3 of the shake holds with room on both platforms.
    public static IEnumerable<object[]> Shakes()
    {
        foreach (float noise in new[] { 0.2f, 0.5f, 1.0f })
        {
            foreach (double rate in new[] { 15.0, 30.0, 60.0 })
                yield return new object[] { 45.0, noise, rate };
            yield return new object[] { 0.0, noise, 30.0 };
        }
    }

    private const int MeanSeeds = 8;

    [Theory]
    [MemberData(nameof(Shakes))]
    public void A_shaking_camera_is_not_amplified_and_does_not_make_the_car_hunt(double physicsRate, float noise, double cameraRate)
    {
        RunResult r = Run("mouselook-hold", physicsRate, noise, cameraRate);
        var late = r.Samples.Where(s => s.T >= 5.0).ToArray();
        var (std, peak, mean) = Wobble(late.Select(HeadingDegrees));
        // The heading the same car keeps with a held camera is its own offset (see the held-camera test): where the motor's grip
        // decayed after the landing's turn. The shake may add to it a bias of up to 0.3 of itself; the offset is allowed on top.
        double heldOffset = Math.Abs(Wobble(Run("mouselook-hold", physicsRate, 0f, cameraRate).Samples.Where(s => s.T >= 5.0).Select(HeadingDegrees)).mean);
        double pooledMean = (mean + Enumerable.Range(2, MeanSeeds - 1).Sum(seed =>
            Wobble(Run("mouselook-hold", physicsRate, noise, cameraRate, o => o.CameraSeed = seed).Samples
                .Where(s => s.T >= 5.0).Select(HeadingDegrees)).mean)) / MeanSeeds;
        double[] windows = Enumerable.Range(0, 4).Select(i =>
            Wobble(late.Where(s => s.T >= 5.0 + 5.0 * i && s.T < 10.0 + 5.0 * i).Select(HeadingDegrees)).std).ToArray();
        _out.WriteLine($"physics {physicsRate,4} Hz  camera shake {noise:0.0} deg at {cameraRate,2} Hz: heading std {std:0.0000} deg, " +
                       $"largest {peak:0.0000} deg, mean {mean:0.0000}, mean of {MeanSeeds} seeds {pooledMean:0.0000}; std in each 5 s window from 5 s: {string.Join(" ", windows.Select(w => w.ToString("0.000")))}");
        Assert.True(std < 0.6 * noise, $"std {std} against a shake of {noise}");
        Assert.True(peak < 1.5 * noise, $"largest {peak} against a shake of {noise}");
        Assert.True(Math.Abs(pooledMean) < 0.3 * noise + heldOffset, $"mean of {MeanSeeds} seeds {pooledMean} against a shake of {noise} and a held camera's offset of {heldOffset}");
        Assert.True(windows.Max() < 0.6 * noise, $"a window's std {windows.Max()} against a shake of {noise}");
    }

    // The 90 degree turn: the camera looks north at the start (the car faces east), the car turns left to face it, with
    // the motors held for 16 s. It overshoots by about a seventh (the aim and the motor's lag make a spring with half
    // critical damping), and the error dies away: by 12 s the heading is within half a degree of the camera's and
    // stays there, shaken or not.
    [Theory]
    [InlineData(45.0, 0f, 30.0)]
    [InlineData(45.0, 0.5f, 30.0)]
    [InlineData(45.0, 1.0f, 15.0)]
    [InlineData(0.0, 0f, 30.0)]
    [InlineData(0.0, 0.5f, 30.0)]
    [InlineData(0.0, 1.0f, 15.0)]
    public void The_ninety_degree_turn_overshoots_and_settles(double physicsRate, float noise, double cameraRate)
    {
        RunResult r = Run("mouselook-turn", physicsRate, noise, cameraRate, o => { o.Hold = 16f; o.Duration = 20f; });
        var hold = r.Samples.Where(s => s.T <= 16.0).ToArray();
        double peak = hold.Max(HeadingDegrees);
        double Err(double t) => HeadingDegrees(hold.First(s => s.T >= t)) - 90.0;
        var late = hold.Where(s => s.T >= 12.0).Select(HeadingDegrees).ToArray();
        var (std, _, mean) = Wobble(late);
        _out.WriteLine($"physics {physicsRate,4} Hz  shake {noise:0.0} deg at {cameraRate,2} Hz: peak heading {peak:0.00} deg (overshoot {peak - 90.0:0.00}); " +
                       $"error at 3 s {Err(3.0):0.00}, 5 s {Err(5.0):0.00}, 7 s {Err(7.0):0.00}, 9 s {Err(9.0):0.00}, 11 s {Err(11.0):0.00}, 13 s {Err(13.0):0.00}, 16 s {Err(16.0):0.00}; " +
                       $"12-16 s: mean error {mean - 90.0:0.000}, std {std:0.0000}");
        Assert.InRange(peak - 90.0, 5.0, 20.0);
        Assert.True(Math.Abs(mean - 90.0) < 0.5, $"mean error {mean - 90.0} over 12-16 s");
        Assert.True(std < 0.5, $"std {std} over 12-16 s");
    }
}
