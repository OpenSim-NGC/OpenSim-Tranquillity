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
/// Dragging a physical object with the mouse, as it behaves today: each run calls SceneObjectGroup.GrabMovement, core's own
/// handler for an ObjectGrabUpdate, on a group whose root part holds the Jolt actor, at the viewer's update rate
/// (<see cref="GrabScenarios.ViewerHz"/>), through the harness at the 11 Hz heartbeat with one physics step per heartbeat and with
/// [Jolt] PhysicsStepRate 45. These tests record what happens (the figures go to the test output, and as CSV rows to the
/// file named by the GRAB_RECON_OUT environment variable when it is set); they assert only that the run finishes, that
/// nothing is not-a-number and that the object was moved at all, which shows the drag reached the actor. Serial with the
/// other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class GrabDragTests
{
    private readonly ITestOutputHelper _output;
    public GrabDragTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in new[] { "grab-side-1kg", "grab-side-100kg", "grab-lift-1kg", "grab-lift-100kg", "grab-side-1kg-still",
                                        "grab-release-1kg", "grab-release-100kg", "grab-pid-side-1kg", "grab-pid-lift-1kg", "grab-pid-release-1kg", "grab-linkset", "grab-wall", "grab-box" })
            foreach (double physicsHz in new[] { 0.0, 45.0 })
                yield return new object[] { name, physicsHz };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_drag_through_core_runs_to_the_end_with_nothing_not_a_number(string scenario, double physicsHz)
    {
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        GrabMeasure m = GrabScenarios.Measure(res);

        _output.WriteLine(GrabMeasure.Header);
        _output.WriteLine(m.Row());
        string path = Environment.GetEnvironmentVariable("GRAB_RECON_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            lock (typeof(GrabDragTests))
                File.AppendAllText(path, m.Row() + "\n");
        }

        float expected = sc.DefaultDuration(0f);
        Assert.False(m.NotANumber, $"{m.Run}: a position or velocity was not a number");
        Assert.True(res.Samples.Count >= (int)(expected * 11.0) - 1, $"{m.Run}: the run stopped after {res.Samples.Count} heartbeats");
        Assert.True(m.Updates > 0, $"{m.Run}: no ObjectGrabUpdate reached core's grab handler");
    }

    /// <summary>The same drag at other update rates: how much of the figures belong to how often the viewer sends.</summary>
    [Theory]
    [InlineData("grab-side-1kg", 15f)]
    [InlineData("grab-side-1kg", 60f)]
    [InlineData("grab-lift-1kg", 15f)]
    [InlineData("grab-lift-1kg", 60f)]
    public void A_drag_at_another_update_rate_runs_to_the_end_with_nothing_not_a_number(string scenario, float updatesPerSecond)
    {
        float before = GrabScenarios.ViewerHz;
        GrabScenarios.ViewerHz = updatesPerSecond;
        try
        {
            foreach (double physicsHz in new[] { 0.0, 45.0 })
                A_drag_through_core_runs_to_the_end_with_nothing_not_a_number(scenario, physicsHz);
        }
        finally
        {
            GrabScenarios.ViewerHz = before;
        }
    }
}
