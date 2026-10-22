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
/// Turning a physical object with the mouse (Ctrl-Shift drag), as it behaves today: each run calls core's own handlers for
/// ObjectSpinStart, ObjectSpinUpdate and ObjectSpinStop on a group whose root part holds the Jolt actor, at the viewer's
/// update rate (<see cref="SpinScenarios.ViewerHz"/>), through the harness at the 11 Hz heartbeat with one physics step per
/// heartbeat and with [Jolt] PhysicsStepRate 45; the script cases also run a look-at or a target omega on the same object.
/// These tests record what happens (the figures go to the test output, and as CSV rows to the file named by the
/// SPIN_RECON_OUT environment variable when it is set); they assert only that the run finishes, that nothing is
/// not-a-number and that the updates reached core's handler. Serial with the other native tests: every run steps a real
/// backend on the shared job pool, and the update rate is a shared setting.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SpinDragTests
{
    private readonly ITestOutputHelper _output;
    public SpinDragTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Drags =
    {
        "spin-90-1kg", "spin-90-100kg", "spin-90-1kg-held", "spin-90-4m-cube", "spin-linkset", "spin-90-1kg-nofriction", "spin-90-1kg-air", "spin-90-1kg-held-air", "spin-90-slow3-1kg-air", "spin-90-slow6-1kg-air", "spin-90-100kg-air", "spin-90-4m-cube-air",
        "spin-release-1kg-air", "spin-release-mid-1kg-air", "spin-release-mid-100kg-air", "spin-linkset-air", "spin-lookat-s1", "spin-lookat-s5", "spin-omega",
        "spin-lookatdrag-1kg", "spin-lookatdrag-1kg-air", "spin-lookatdrag-100kg-air", "spin-lookatdrag-4m-cube-air", "spin-lookatdrag-s2-1kg-air", "spin-lookatdrag-release-1kg-air",
        "spin-lookatdrag-release-mid-1kg-air", "spin-lookatdrag-slow6-1kg-air", "spin-lookatdrag-linkset-air", "spin-lookatdrag-linkset",
    };
    private static readonly string[] AloneScripts = { "spin-lookat-alone", "spin-lookat-alone-ground", "spin-omega-alone" };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in Drags.Concat(AloneScripts))
            foreach (double physicsHz in new[] { 0.0, 45.0 })
                yield return new object[] { name, physicsHz };
    }

    private void RunOne(string scenario, double physicsHz, bool simulatorDefaults = false)
    {
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, SimulatorDefaults = simulatorDefaults });
        SpinMeasure m = SpinScenarios.Measure(res);
        if (simulatorDefaults)
            m.Run += "/simdefaults";

        _output.WriteLine(SpinMeasure.Header);
        _output.WriteLine(m.Row());
        string path = Environment.GetEnvironmentVariable("SPIN_RECON_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            lock (typeof(SpinDragTests))
                File.AppendAllText(path, m.Row() + "\n");
        }

        float expected = sc.DefaultDuration(0f);
        Assert.False(m.NotANumber, $"{m.Run}: a position, rotation or angular velocity was not a number");
        Assert.True(res.Samples.Count >= (int)(expected * 11.0) - 1, $"{m.Run}: the run stopped after {res.Samples.Count} heartbeats");
        if (!AloneScripts.Contains(scenario))
            Assert.True(m.Updates > 0, $"{m.Run}: no ObjectSpinUpdate reached core's spin handler");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_spin_through_core_runs_to_the_end_with_nothing_not_a_number(string scenario, double physicsHz) => RunOne(scenario, physicsHz);

    /// <summary>The prim values the simulator gives a new prim (wood, friction 0.6): the same turn on the ground and clear of it.</summary>
    [Theory]
    [InlineData("spin-90-1kg")]
    [InlineData("spin-90-1kg-air")]
    [InlineData("spin-lookat-s1")]
    [InlineData("spin-omega")]
    [InlineData("spin-lookatdrag-1kg")]
    [InlineData("spin-lookatdrag-1kg-air")]
    public void A_spin_with_the_simulators_prim_values_runs_to_the_end_with_nothing_not_a_number(string scenario)
    {
        foreach (double physicsHz in new[] { 0.0, 45.0 })
            RunOne(scenario, physicsHz, simulatorDefaults: true);
    }

    /// <summary>The same turn at other update rates: how much of the figures belong to how often the viewer sends.</summary>
    [Theory]
    [InlineData("spin-90-1kg", 15f)]
    [InlineData("spin-90-1kg", 60f)]
    [InlineData("spin-90-1kg-air", 15f)]
    [InlineData("spin-90-1kg-air", 60f)]
    [InlineData("spin-90-100kg-air", 15f)]
    [InlineData("spin-90-100kg-air", 60f)]
    [InlineData("spin-release-1kg-air", 15f)]
    [InlineData("spin-release-1kg-air", 60f)]
    [InlineData("spin-lookatdrag-1kg-air", 15f)]
    [InlineData("spin-lookatdrag-1kg-air", 60f)]
    [InlineData("spin-lookatdrag-1kg", 15f)]
    [InlineData("spin-lookatdrag-1kg", 60f)]
    public void A_spin_at_another_update_rate_runs_to_the_end_with_nothing_not_a_number(string scenario, float updatesPerSecond)
    {
        float before = SpinScenarios.ViewerHz;
        SpinScenarios.ViewerHz = updatesPerSecond;
        try
        {
            foreach (double physicsHz in new[] { 0.0, 45.0 })
                RunOne(scenario, physicsHz);
        }
        finally
        {
            SpinScenarios.ViewerHz = before;
        }
    }
}
