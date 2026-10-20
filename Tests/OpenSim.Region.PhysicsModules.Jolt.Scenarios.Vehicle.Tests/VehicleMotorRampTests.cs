/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>Driven vehicles through the harness at the four heartbeat rates (serial: real backends on the shared pool).</summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleMotorDriveTests
{
    private const double ReferenceRate = 720.0;

    private static List<Sample> Drive(string scenario, double rate, string presets)
    {
        // A 4 s hold lands on a heartbeat at every rate (a whole number of steps at 11, 22.5, 45 and 90 Hz).
        var o = new HarnessOptions { RateHz = rate, SlopeDeg = 0f, Hold = 4f, Duration = 6f, Jolt = { ["VehiclePresets"] = presets } }.SecondLife();
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o).Samples;
    }

    private static float SpeedAt(List<Sample> samples, double t)
    {
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].T >= t - 1e-9)
            {
                Sample a = samples[i - 1], b = samples[i];
                return (float)(a.Speed + (b.Speed - a.Speed) * (t - a.T) / (b.T - a.T));
            }
        return samples[^1].Speed;
    }

    // The car preset and the boat preset on level ground and water: the motor ramp under the key (1-4 s) and its
    // braking after the key is let go (5 s), each rate within 1% of a 720 Hz heartbeat.
    [Theory]
    [InlineData("car", "documented")]
    [InlineData("car", "legacy")]
    [InlineData("boat", "legacy")]
    public void The_same_drive_gives_the_same_speeds_at_every_rate(string scenario, string presets)
    {
        List<Sample> reference = Drive(scenario, ReferenceRate, presets);
        foreach (double rate in Harness.Harness.Rates)
        {
            List<Sample> run = Drive(scenario, rate, presets);
            foreach (double t in new[] { 1.0, 2.0, 3.0, 4.0, 5.0 })
            {
                float v = SpeedAt(run, t), vr = SpeedAt(reference, t);
                Assert.True(Math.Abs(v - vr) <= 0.01f * Math.Max(vr, 0.05f), $"{scenario} at {rate} Hz, t = {t}: {v:0.000} against {vr:0.000}");
            }
        }
    }
}
