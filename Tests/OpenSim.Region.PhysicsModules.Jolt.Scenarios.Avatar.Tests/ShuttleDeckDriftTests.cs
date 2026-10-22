/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical deck set up as a platform script sets one up (a sled vehicle that cannot tilt or turn, driven along its x axis) goes
/// out and comes back along its line: it does not slide sideways after it turns round. Each figure is a line "DRIFT ..." in the
/// test output. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ShuttleDeckDriftTests
{
    private readonly ITestOutputHelper _out;
    public ShuttleDeckDriftTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("InWorldz", 0.0, true)]
    [InlineData("InWorldz", 45.0, true)]
    [InlineData("InWorldz", 0.0, false)]
    [InlineData("InWorldz", 45.0, false)]
    [InlineData("SecondLife", 0.0, true)]
    [InlineData("SecondLife", 45.0, true)]
    public void A_driven_deck_keeps_its_line_after_it_turns_round(string behaviour, double rate, bool rider)
    {
        RunResult res = KinematicTestRig.Run(ShuttleScenarios.Ride(true, 0f, rider: rider), rate, ("VehicleBehaviour", behaviour));
        var d = ShuttleScenarios.Drift(res);
        Say($"DRIFT {behaviour} rate {rate:0} rider {rider}: off the line by up to {F(d.MaxOffLine)} m (first past 0.5 m at {F(d.FirstOffLineT)} s), "
            + $"sideways speed up to {F(d.MaxSideSpeed)} m/s, heading off +x by up to {F(d.MaxHeadingDeg)} degrees");
        Assert.True(d.MaxOffLine < 0.5, $"the deck left its line by {F(d.MaxOffLine)} m (sideways speed up to {F(d.MaxSideSpeed)} m/s)");
    }
}
