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
/// An avatar standing on a deck that goes 44 m out at 3 m/s, slows over its last 7 m, waits 8 s and comes back (a platform ride
/// as a vehicle script drives one, or keyframed motion plays one) stays on the deck, and when the deck has stopped it stands
/// still on it: in one region, and when the deck crosses the region's border with the rider on it. Each figure is a line
/// "RIDE ..." in the test output.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RiderOnMovingDeckTests
{
    private readonly ITestOutputHelper _out;
    public RiderOnMovingDeckTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static IEnumerable<object[]> InRegionCases()
    {
        foreach (bool physical in new[] { true, false })
            foreach (string behaviour in new[] { "InWorldz", "SecondLife" })
                foreach (double rate in new[] { 0.0, 45.0 })
                    foreach (float ahead in new[] { 0f, 4.4f })
                        yield return new object[] { physical, behaviour, rate, ahead };
    }

    public static IEnumerable<object[]> CrossingCases()
    {
        foreach (bool physical in new[] { true, false })
            foreach (string behaviour in new[] { "InWorldz", "SecondLife" })
                foreach (double rate in new[] { 0.0, 45.0 })
                    foreach (float ahead in new[] { 0f, 4.4f })
                        yield return new object[] { physical, behaviour, rate, ahead };
    }

    private ShuttleScenarios.Figures Ride(bool physical, string behaviour, double rate, float ahead, bool linkset = false, float drop = 0f, bool crossing = false)
    {
        RunResult res = KinematicTestRig.Run(ShuttleScenarios.Ride(physical, ahead, linkset, drop, crossing), rate, ("VehicleBehaviour", behaviour));
        ShuttleScenarios.Figures f = ShuttleScenarios.Measure(res);
        Say($"RIDE {(physical ? "physical" : "keyframed")}{(linkset ? " linkset" : "")}{(drop > 0f ? $" drop {drop:0.#}" : "")}{(crossing ? " crossing" : "")} {behaviour} rate {rate:0} ahead {ahead:0.0}: "
            + $"{f.Verdict}, deck stopped {F(f.DeckStopT)} s, slid back {F(f.MaxSlideBack)} ahead {F(f.MaxAhead)}, "
            + $"end {F(f.EndDistance)} m from the middle (dz {F(f.EndRelZ)}), speed against the deck while it is stopped {F(f.SpeedWhileStopped)} m/s");
        return f;
    }

    // deckMoves: false for a physical sled under SecondLife, whose 29-tonne deck does not move at all (the motor accelerates it by less than the friction holds it)
    private static void AssertRiderStays(ShuttleScenarios.Figures f, bool deckMoves = true)
    {
        Assert.True(f.OnAtEnd, $"the rider is not on the deck at the end: {f.Verdict}, {F(f.EndDistance)} m from its middle");
        Assert.True(!deckMoves || !double.IsNaN(f.DeckStopT), "the deck never stopped, so the run says nothing about a rider on a stopped deck");
        Assert.True(f.SpeedWhileStopped < 0.3, $"the rider moves {F(f.SpeedWhileStopped)} m/s over the stopped deck");
        Assert.True(double.IsNaN(f.LostContactT), $"the rider was off the deck from {F(f.LostContactT)} s");
    }

    [Theory]
    [MemberData(nameof(InRegionCases))]
    public void A_standing_rider_stays_on_a_deck_that_goes_out_and_back(bool physical, string behaviour, double rate, float ahead)
        => AssertRiderStays(Ride(physical, behaviour, rate, ahead), deckMoves: !physical || behaviour == "InWorldz");

    [Theory]
    [InlineData("InWorldz", 0.0, 0f)]
    [InlineData("InWorldz", 45.0, 0f)]
    [InlineData("InWorldz", 0.0, 2.5f)]
    [InlineData("InWorldz", 45.0, 2.5f)]
    [InlineData("InWorldz", 45.0, 4.4f)]
    public void A_standing_rider_stays_on_a_linked_deck_that_goes_out_and_back(string behaviour, double rate, float ahead)
        => AssertRiderStays(Ride(true, behaviour, rate, ahead, linkset: true));

    [Theory]
    [InlineData(true, 0.0, 2.6f)]
    [InlineData(true, 45.0, 2.6f)]
    [InlineData(false, 0.0, 2.6f)]
    [InlineData(false, 45.0, 2.6f)]
    public void A_rider_dropped_onto_the_deck_stays_on_it(bool physical, double rate, float drop)
        => AssertRiderStays(Ride(physical, "InWorldz", rate, 2.5f, linkset: physical, drop: drop));

    [Theory]
    [MemberData(nameof(CrossingCases))]
    public void A_standing_rider_stays_on_a_deck_that_crosses_the_border_and_comes_back(bool physical, string behaviour, double rate, float ahead)
        => AssertRiderStays(Ride(physical, behaviour, rate, ahead, crossing: true), deckMoves: !physical || behaviour == "InWorldz");

    [Theory]
    [InlineData("InWorldz", 0.0, 0f)]
    [InlineData("InWorldz", 45.0, 0f)]
    [InlineData("InWorldz", 45.0, 4.4f)]
    public void A_standing_rider_stays_on_a_linked_deck_that_crosses_the_border(string behaviour, double rate, float ahead)
        => AssertRiderStays(Ride(true, behaviour, rate, ahead, linkset: true, crossing: true));
}
