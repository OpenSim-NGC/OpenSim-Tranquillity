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
/// The Jolt scene says it has a rotation target (<c>PhysicsScene.SupportsRotationTarget</c>), so core's own handlers for a
/// script's llRotLookAt and for the rotate-drag (ObjectSpinStart, ObjectSpinUpdate, ObjectSpinStop) give the root's actor
/// the target and core's per-frame controller does not set an angular velocity of its own. Each run puts a core scene
/// object group around a Jolt body whose scene is the harness's JoltScene, and steps it at the 11 Hz heartbeat, with one
/// physics step per heartbeat and with 45 steps a second. Serial with the other native tests: every run steps a real
/// backend on the shared job pool, and the update rate is a shared setting.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RotationJoinTests
{
    private readonly ITestOutputHelper _output;
    public RotationJoinTests(ITestOutputHelper output) => _output = output;

    // llRotLookAt's target is Second Life's documented one; the shipped default, BasicForcesBehaviour = InWorldz, aims by
    // its own law, so this run pins the choice.
    private static SpinMeasure Run(string scenario, double physicsHz)
    {
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, Jolt = { ["BasicForcesBehaviour"] = "SecondLife" } });
        return SpinScenarios.Measure(res);
    }

    [Fact]
    public void The_jolt_scene_says_it_has_a_rotation_target()
    {
        bool answer = false;
        var sc = new Scenario
        {
            Name = "rotation-target-question",
            DefaultDuration = _ => 0.5f,
            Setup = r =>
            {
                r.AddBox(new OpenMetaverse.Vector3(1f, 1f, 1f), new OpenMetaverse.Vector3(128f, 128f, r.GroundAt(128f, 128f) + 0.52f), OpenMetaverse.Quaternion.Identity);
                answer = r.PhysicsScene.SupportsRotationTarget();
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0 });
        Assert.True(answer);
    }

    /// <summary>A script's llRotLookAt, called on core's group, on a cube resting on the ground: the engine's target takes it
    /// to the rotation (core's own controller stopped 6 to 8 degrees short there) and core sets no angular velocity.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_scripts_llRotLookAt_on_the_ground_reaches_the_engines_target_and_core_does_not_steer(double physicsHz)
    {
        SpinMeasure m = Run("spin-lookat-alone-ground", physicsHz);
        _output.WriteLine(m.Row());
        Assert.False(m.CoreWroteSpin, "core's controller set an angular velocity on the part");
        Assert.True(Math.Abs(m.EndError) < 2f, $"ended {m.EndError:0.0} degrees from the target");
        Assert.True(m.Overshoot < 0.5f, $"passed the target by {m.Overshoot:0.0} degrees");
    }

    /// <summary>A rotate-drag through core's spin handlers on a cube resting on the ground, 90 degrees in 1 s: the target moves
    /// with each update, the body ends on the cursor's rotation, and core's controller does nothing.</summary>
    [Theory]
    [InlineData("spin-90-1kg", 0.0)]
    [InlineData("spin-90-1kg", 45.0)]
    [InlineData("spin-linkset", 45.0)]
    public void A_rotate_drag_on_the_ground_reaches_the_engines_target_and_core_does_not_steer(string scenario, double physicsHz)
    {
        SpinMeasure m = Run(scenario, physicsHz);
        _output.WriteLine(m.Row());
        Assert.True(m.Updates > 0, "no update reached core's handler");
        Assert.False(m.CoreWroteSpin, "core's controller set an angular velocity on the part");
        Assert.True(Math.Abs(m.EndError) < 2f, $"ended {m.EndError:0.0} degrees from the cursor");
        Assert.True(m.Overshoot < 0.5f, $"passed the cursor by {m.Overshoot:0.0} degrees");
    }

    /// <summary>A script's llTargetOmega of 1 rad/s (57.3 degrees a second) on the object while a rotate-drag turns it: the drag
    /// owns the turn, and when it is let go core applies the script's spin again. Switching the engine's target off must not
    /// take that spin away: it sets the angular velocity to zero only when nothing else has written one since the last step.
    /// Clear of the ground, where nothing but the body's own damping slows it.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_scripts_llTargetOmega_is_applied_again_when_the_rotate_drag_is_let_go(double physicsHz)
    {
        SpinMeasure m = Run("spin-omega-release", physicsHz);
        _output.WriteLine(m.Row());
        // 57.3 degrees a second at the release, less the body's angular damping over the 5 s watched afterwards (about 43)
        Assert.InRange(m.EndDegPerSec, 40f, 60f);
    }
}

/// <summary>
/// The figures of a script's rotation calls and the rotate-drag with the Jolt scene answering true: every run goes through
/// core's own handlers (see <see cref="RotationJoinTests"/>), at one physics step per heartbeat and at 45 steps a second.
/// These tests record what happens (the figures go to the test output, and as CSV rows to the file named by the
/// SPIN_RECON_OUT environment variable when it is set); they assert only that the run finishes, that nothing is
/// not-a-number, and that the updates reached core's handler. Serial with the other native tests, and the update rate is a
/// shared setting.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RotationJoinFigureTests
{
    private readonly ITestOutputHelper _output;
    public RotationJoinFigureTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Scenarios =
    {
        "spin-90-1kg", "spin-90-100kg", "spin-90-4m-cube", "spin-linkset", "spin-90-slow6-1kg", "spin-release-1kg", "spin-release-mid-1kg",
        "spin-omega-ground", "spin-omega-release-ground", "spin-omega-release", "spin-lookat-alone-ground", "spin-lookat-linkset-child", "spin-with-grab",
        "spin-90-1kg-air", "spin-90-100kg-air", "spin-90-4m-cube-air", "spin-linkset-air", "spin-90-slow6-1kg-air", "spin-release-1kg-air",
        "spin-release-mid-1kg-air", "spin-omega", "spin-lookat-alone", "spin-lookat-s1",
    };

    private static readonly string[] NoDragScenarios = { "spin-lookat-alone-ground", "spin-lookat-linkset-child", "spin-lookat-alone", "spin-omega-ground", "spin-omega" };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string name in Scenarios)
            foreach (double physicsHz in new[] { 0.0, 45.0 })
                yield return new object[] { name, physicsHz };
    }

    private void RunOne(string scenario, double physicsHz)
    {
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        SpinMeasure m = SpinScenarios.Measure(res);
        _output.WriteLine(SpinMeasure.Header);
        _output.WriteLine(m.Row());
        string path = Environment.GetEnvironmentVariable("SPIN_RECON_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            lock (typeof(RotationJoinFigureTests))
                File.AppendAllText(path, m.Row() + "\n");
        }

        Assert.False(m.NotANumber, $"{m.Run}: a position, rotation or angular velocity was not a number");
        Assert.True(res.Samples.Count >= (int)(sc.DefaultDuration(0f) * 11.0) - 1, $"{m.Run}: the run stopped after {res.Samples.Count} heartbeats");
        if (!NoDragScenarios.Contains(scenario))
            Assert.True(m.Updates > 0, $"{m.Run}: no ObjectSpinUpdate reached core's spin handler");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_turn_through_core_with_the_engines_target_runs_to_the_end_with_nothing_not_a_number(string scenario, double physicsHz) => RunOne(scenario, physicsHz);

    [Theory]
    [InlineData("spin-90-1kg", 15f)]
    [InlineData("spin-90-1kg", 60f)]
    [InlineData("spin-90-1kg-air", 15f)]
    [InlineData("spin-90-1kg-air", 60f)]
    public void A_turn_at_another_update_rate_runs_to_the_end_with_nothing_not_a_number(string scenario, float updatesPerSecond)
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
