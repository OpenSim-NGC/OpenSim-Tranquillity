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
/// An avatar's jump follows the path its launch speed gives under constant gravity, at every heartbeat rate.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarJumpTests
{
    private const float LaunchSpeed = 4.0f;        // [Jolt] AvatarJumpSpeed default
    private const float Gravity = 9.80665f;

    [Theory]
    [InlineData(11.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_jump_follows_its_launch_speed_at_any_rate(double rate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("avatar-jump"), new HarnessOptions { RateHz = rate });
        // The jump is pressed at the first heartbeat at or after 1 s and launches with that step.
        double launch = Math.Ceiling(1.0 * rate - 1e-9) / rate;
        float z0 = r.Samples.Last(s => s.T <= launch + 1e-9).Position.Z;
        float peak = LaunchSpeed * LaunchSpeed / (2f * Gravity);   // 0.816 m

        foreach (Sample s in r.Samples)
        {
            double t = s.T - launch;
            if (t <= 0 || t >= 2 * LaunchSpeed / Gravity - 0.05)
                continue;   // before the jump, or about to land
            float expected = z0 + (float)(LaunchSpeed * t - 0.5 * Gravity * t * t);
            Assert.True(Math.Abs(s.Position.Z - expected) < 0.002f, $"{rate} Hz, {t:0.000} s after launch: z {s.Position.Z:0.0000}, the path gives {expected:0.0000}");
        }

        // The highest sample is the peak, short of it only by where the samples fall around the top (at most
        // g * dt^2 / 8: 6 mm at 11 Hz).
        float slack = (float)(Gravity / (8 * rate * rate)) + 0.001f;
        Assert.InRange(r.Summary.PeakRise, peak - slack, peak + 0.001f);
    }
}
