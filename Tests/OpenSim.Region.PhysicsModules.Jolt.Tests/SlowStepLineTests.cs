/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physics step slower than JoltScene.SlowStepWarnMs gets a warning line of its own at once, naming what the region held
/// (bodies, active bodies, shapes, avatars, the update error, the pool wait), so a slow step just before a native fault is in
/// the log with its detail. Pure: no native library, runs in parallel.
/// </summary>
public class SlowStepLineTests
{
    private static StepResult Step(int active, float ms) => new(3, 0, 7, false, false, active, ms);

    private static PhysicsCapacityStats Stats() => new()
    {
        LiveBodyCount = 12,
        LiveShapeCount = 9,
        ActiveBodyCount = 2,
        CharacterCount = 0,
        LastUpdateError = PhysicsUpdateErrors.None,
        UpdateGateWaitMsMax = 61.84,
    };

    [Fact]
    public void A_step_under_the_limit_has_no_line()
    {
        PhysicsCapacityStats s = Stats();
        StepResult r = Step(2, JoltScene.SlowStepWarnMs - 1f);
        Assert.Null(JoltScene.SlowStepLine("Region A", JoltScene.SlowStepWarnMs - 1f, 1f / 11f, 100, in r, 7, in s, 0));
    }

    [Fact]
    public void A_slow_step_names_the_region_its_time_and_what_the_region_held()
    {
        PhysicsCapacityStats s = Stats();
        StepResult r = Step(2, 61.77f);
        string line = JoltScene.SlowStepLine("Region A", 61.77f, 1f / 11f, 4242, in r, 7, in s, 3);
        Assert.NotNull(line);
        Assert.Contains("Region A", line);
        Assert.Contains("61.77 ms", line);
        Assert.Contains("step 4242", line);
        Assert.Contains("active=2", line);
        Assert.Contains("bodies=12", line);
        Assert.Contains("shapes=9", line);
        Assert.Contains("avatars=0", line);
        Assert.Contains("contacts=7", line);
        Assert.Contains("updateError=None", line);
        Assert.Contains("poolWaitMax=61.84 ms", line);
        Assert.Contains("3 more held back", line);
    }

    // A region's first steps build the broad phase and wake every loaded body: on a live start the first step of two regions
    // took 74.19 ms and 66.24 ms with nothing wrong. Those steps get no line; the limit is unchanged after them.
    [Theory]
    [InlineData(1, 74.19f)]
    [InlineData(1, 66.24f)]
    [InlineData(JoltScene.SlowStepWarmupSteps, 500f)]
    public void A_slow_step_among_the_first_after_start_has_no_line(long step, float ms)
    {
        PhysicsCapacityStats s = Stats();
        StepResult r = Step(2, ms);
        Assert.Null(JoltScene.SlowStepLine("Region A", ms, 1f / 11f, step, in r, 7, in s, 0));
    }

    [Fact]
    public void The_first_slow_step_after_the_start_steps_has_its_line_at_the_same_limit()
    {
        PhysicsCapacityStats s = Stats();
        long first = JoltScene.SlowStepWarmupSteps + 1;
        StepResult r = Step(2, JoltScene.SlowStepWarnMs);
        string line = JoltScene.SlowStepLine("Region A", JoltScene.SlowStepWarnMs, 1f / 11f, first, in r, 7, in s, 0);
        Assert.NotNull(line);
        Assert.Contains($"step {first}", line);
        StepResult under = Step(2, JoltScene.SlowStepWarnMs - 0.01f);
        Assert.Null(JoltScene.SlowStepLine("Region A", JoltScene.SlowStepWarnMs - 0.01f, 1f / 11f, first, in under, 7, in s, 0));
    }
}
