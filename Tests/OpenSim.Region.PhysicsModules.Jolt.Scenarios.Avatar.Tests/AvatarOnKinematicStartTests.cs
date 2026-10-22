/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar on a platform that starts to move takes the platform's velocity in the same step. The platform's body is made again
/// when the motion starts (a fixed body cannot become a kinematic one); the avatar had the removed body for its ground, so for the
/// first step it found no ground velocity and was left behind by what the platform moved in that step: 1.5 m after ten heartbeats
/// at 16.5 m/s (0.38 m at 45 Hz). Each figure is a line "START ..." in the test output.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarOnKinematicStartTests
{
    private readonly ITestOutputHelper _out;
    public AvatarOnKinematicStartTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    // The platform is set 1.5 m along x in each of ten heartbeats (16.5 m/s at 11 Hz) with the hint turned on at the first of them:
    // an abrupt fast start of the platform, with no ramp-up. The offset is read two heartbeats after the last set.
    private double OffsetAfterAbruptStart(double physicsRate, bool hintFirst, float stepMetres = 1.5f, params (string, string)[] jolt)
    {
        double across = double.NaN;
        var sc = KinematicTestRig.Platform("avatar", 6f, r =>
        {
            if (r.Heartbeats == (hintFirst ? 12 : 22))
                r.Other.MovedByKeyframedMotion = true;   // hintFirst: the platform has been kinematic and at rest ten heartbeats already
            if (r.Heartbeats >= 22 && r.Heartbeats < 32)
                r.Other.Position += new Vector3(stepMetres, 0f, 0f);
            if (r.Heartbeats == 33)
                across = Math.Abs(r.Actor.Position.X - r.Other.Position.X);
        });
        KinematicTestRig.Run(sc, physicsRate, jolt);
        return across;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_takes_the_platforms_velocity_in_the_step_the_motion_starts(double physicsRate)
    {
        double across = OffsetAfterAbruptStart(physicsRate, hintFirst: false);
        Say($"START abrupt 16.5 m/s rate {physicsRate:0}: avatar {F(across)} m from the middle of the platform after ten heartbeats");
        Assert.True(across < 0.1, $"the avatar was {across:0.000} m from the middle of the platform");
    }

    [Theory]
    [InlineData(2.0f, 0.0)]
    [InlineData(2.7f, 0.0)]
    [InlineData(3.6f, 0.0)]
    [InlineData(2.7f, 45.0)]
    public void An_avatar_takes_a_start_of_up_to_the_jump_distance_a_heartbeat_in_the_same_step(float stepMetres, double physicsRate)
    {
        double across = OffsetAfterAbruptStart(physicsRate, hintFirst: false, stepMetres);
        Say($"START abrupt {stepMetres / (1.0 / 11.0):0.0} m/s rate {physicsRate:0}: avatar {F(across)} m from the middle of the platform after ten heartbeats");
        Assert.True(across < 0.1, $"the avatar was {across:0.000} m from the middle of the platform");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_on_a_platform_that_was_already_kinematic_is_in_step_as_before(double physicsRate)
    {
        double across = OffsetAfterAbruptStart(physicsRate, hintFirst: true);
        Say($"START abrupt 16.5 m/s, platform kinematic and at rest first, rate {physicsRate:0}: avatar {F(across)} m from the middle");
        Assert.True(across < 0.1, $"the avatar was {across:0.000} m from the middle of the platform");
    }

}
