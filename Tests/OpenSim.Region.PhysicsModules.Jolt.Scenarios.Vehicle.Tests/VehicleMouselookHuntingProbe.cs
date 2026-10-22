/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A measurement aid, skipped by default (it takes about a minute): the heading mean of the shaking-camera run for 40
/// camera seeds at each shake and rate, with the spread of those means. It is the evidence behind the mean of eight
/// seeds in <see cref="VehicleMouselookHuntingTests"/>. Remove the Skip to run it.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleMouselookHuntingProbe
{
    private readonly ITestOutputHelper _out;
    public VehicleMouselookHuntingProbe(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "Measurement aid; remove the Skip to print the heading means of 40 camera seeds")]
    public void Probe_prints_every_shake_case()
    {
        const double Rad = 180.0 / Math.PI;
        var sb = new StringBuilder();
        (double mean, double std, double peak, double[] wm) Measure(double phys, float noise, double rate, int seed)
        {
            var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = phys, CameraNoiseDegrees = noise, CameraRateHz = rate, CameraSeed = seed };
            RunResult r = Harness.Harness.Run(Harness.Harness.Find("mouselook-hold"), o);
            var late = r.Samples.Where(s => s.T >= 5.0).ToArray();
            double[] h = late.Select(s => Harness.Harness.Heading(s.Rotation) * Rad).ToArray();
            double mean = h.Average();
            double std = Math.Sqrt(h.Select(x => (x - mean) * (x - mean)).Average());
            double[] wm = Enumerable.Range(0, 4).Select(i => late.Where(s => s.T >= 5.0 + 5.0 * i && s.T < 10.0 + 5.0 * i).Select(s => Harness.Harness.Heading(s.Rotation) * Rad).DefaultIfEmpty(double.NaN).Average()).ToArray();
            return (mean, std, h.Max(x => Math.Abs(x - mean)), wm);
        }
        foreach ((double phys, double rate) in new[] { (45.0, 15.0), (45.0, 30.0), (45.0, 60.0), (0.0, 30.0) })
        {
            foreach (float noise in new[] { 0.2f, 1.0f })
            {
                var means = new List<double>(); var stds = new List<double>(); var wmeans = new List<double>();
                for (int seed = 1; seed <= 40; seed++)
                {
                    var m = Measure(phys, noise, rate, seed);
                    means.Add(m.mean); stds.Add(m.std); wmeans.AddRange(m.wm);
                    sb.AppendLine($"PROBE seed {seed} noise {noise:0.0} phys {phys,2} cam {rate,2}: mean {m.mean:0.0000} std {m.std:0.0000} peak {m.peak:0.0000} windowMeans {string.Join(" ", m.wm.Select(w => w.ToString("0.0000")))}");
                }
                double mm = means.Average(), sm = Math.Sqrt(means.Select(x => (x - mm) * (x - mm)).Average());
                double ms = stds.Average();
                double wmm = wmeans.Average(), wms = Math.Sqrt(wmeans.Select(x => (x - wmm) * (x - wmm)).Average());
                sb.AppendLine($"PROBE SUMMARY40 noise {noise:0.0} phys {phys,2} cam {rate,2}: mean of means {mm:0.0000} sd of means {sm:0.0000} | mean std {ms:0.0000} | mean of 5s-window means {wmm:0.0000} sd {wms:0.0000} | seeds with |mean|>{0.3 * noise:0.000}: {means.Count(x => Math.Abs(x) > 0.3 * noise)} of 40 | min {means.Min():0.0000} max {means.Max():0.0000}");
            }
        }
        // The camera held on the heading, for reference.
        {
            var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0, CameraNoiseDegrees = 0f, CameraRateHz = 30.0 };
            RunResult r = Harness.Harness.Run(Harness.Harness.Find("mouselook-hold"), o);
            double[] h = r.Samples.Where(s => s.T >= 5.0).Select(s => Harness.Harness.Heading(s.Rotation) * Rad).ToArray();
            sb.AppendLine($"PROBE HELD 45Hz: mean {h.Average():0.0000} min {h.Min():0.0000} max {h.Max():0.0000} n {h.Length}");
        }
        _out.WriteLine(sb.ToString());
    }
}
