/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script's average time is the mean of its last 16 timeslices, recorded by the run loop after each slice (Halcyon
/// LSLSystemAPI.AddExecutionTime / GetAverageScriptTime). It was always 0.
/// </summary>
// Runs in parallel: its own harness.
public class AverageScriptTimeTests
{
    [Fact]
    public void AScriptThatRanHasAnAverageTimeAboveZero()
    {
        const string src = "integer n; default { state_entry() { integer i; for (i = 0; i < 3000; i++) n += i; llSay(0, \"done\"); } }";
        using var h = new SchedulerHarness();
        var item = h.RezScript(src);
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), SavedStateRig.SaidText(h));
        var interp = (InWorldz.Phlox.VM.Interpreter)h.InterpreterFor(item);
        Assert.True(interp.GetAverageScriptTime() > 0f, "average script time is still 0 after the script ran");
    }

    [Fact]
    public void AScriptThatNeverRanHasNoAverageTime()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { } }", UUID.Random(), UUID.Random(), running: false);
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        var interp = (InWorldz.Phlox.VM.Interpreter)h.InterpreterFor(item);
        Assert.NotNull(interp);
        Assert.Equal(0f, interp.GetAverageScriptTime());
    }
}
